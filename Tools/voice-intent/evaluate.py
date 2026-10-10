# /// script
# requires-python = ">=3.10"
# dependencies = ["torch", "transformers>=4.57", "accelerate", "huggingface_hub"]
# ///
"""Scores a FunctionGemma model on the held-out rows: is every function call exactly right?

  uv run evaluate.py --model USER/doorway-functiongemma --data data          the fine-tuned model
  uv run evaluate.py --model google/functiongemma-270m-it --data data        the untuned baseline
  hf jobs uv run --flavor t4-small --secrets HF_TOKEN evaluate.py --model USER/doorway-functiongemma \
      --data USER/doorway-voice-intents

A 270M model is fine on a laptop CPU too, just slower. Reports accuracy by source (template phrasings held out of
training, paraphrases, the hand-written set) and by intent, the misses, and the time per answer. Self-contained,
like train.py.
"""
import argparse
import json
import os
import re
import time

import torch
from huggingface_hub import hf_hub_download
from transformers import AutoModelForCausalLM, AutoTokenizer

CALL = re.compile(r"<start_function_call>call:(\w+)\{(.*?)\}<end_function_call>", re.S)
ARGUMENT = re.compile(r"(\w+):(?:<escape>(.*?)<escape>|([^,}]*))", re.S)


def data_file(data, name):
    if os.path.isdir(data):
        path = os.path.join(data, name)
        return path if os.path.exists(path) else None
    try:
        return hf_hub_download(data, name, repo_type="dataset")
    except Exception:
        return None


def read_rows(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def prompt_messages(row, developer):
    messages = [{"role": "developer", "content": developer}]
    if row.get("context"):
        messages.append({"role": "assistant", "content": row["context"]})
    messages.append({"role": "user", "content": row["text"]})
    return messages


def parse_calls(text):
    """The model's answer as [(name, {argument: value})]. Values compare without case: the app matches ids so."""
    calls = []
    for name, body in CALL.findall(text):
        arguments = {k: (a if a else b).strip().lower() for k, a, b in ARGUMENT.findall(body)}
        calls.append((name, arguments))
    return calls


def expected(row):
    return [(c["name"], {k: str(v).lower() for k, v in c["arguments"].items()}) for c in row["calls"]]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True)
    parser.add_argument("--data", required=True, help="local data folder or dataset repo id")
    parser.add_argument("--limit", type=int, default=0, help="score only this many rows (0: all)")
    parser.add_argument("--show", type=int, default=25, help="misses to print")
    args = parser.parse_args()

    with open(data_file(args.data, "tools.json"), encoding="utf-8") as f:
        spec = json.load(f)
    rows = read_rows(data_file(args.data, "test.jsonl"))
    handwritten = data_file(args.data, "handwritten.jsonl") or data_file(os.path.dirname(os.path.abspath(__file__)),
                                                                         "handwritten.jsonl")
    if handwritten:
        rows += [dict(r, source="handwritten") for r in read_rows(handwritten)]
    if args.limit:
        rows = rows[:args.limit]

    device = "cuda" if torch.cuda.is_available() else "cpu"
    tokenizer = AutoTokenizer.from_pretrained(args.model)
    model = AutoModelForCausalLM.from_pretrained(args.model, dtype="auto", attn_implementation="eager").to(device)
    model.eval()
    # A call ends with <start_function_response> (the app would run the function and reply), not the end of the
    # text: stop there, or every answer runs on to max_new_tokens.
    stops = [tokenizer.eos_token_id] + [tokenizer.convert_tokens_to_ids(t)
                                         for t in ("<start_function_response>", "<end_of_turn>")
                                         if tokenizer.convert_tokens_to_ids(t) != tokenizer.unk_token_id]

    totals, misses, seconds = {}, [], 0.0
    for row in rows:
        inputs = tokenizer.apply_chat_template(prompt_messages(row, spec["developer"]), tools=spec["tools"],
                                               add_generation_prompt=True, return_dict=True, return_tensors="pt")
        start = time.perf_counter()
        with torch.no_grad():
            out = model.generate(**inputs.to(device), pad_token_id=tokenizer.eos_token_id, max_new_tokens=96,
                                 do_sample=False, eos_token_id=stops)
        seconds += time.perf_counter() - start
        answer = tokenizer.decode(out[0][inputs["input_ids"].shape[1]:], skip_special_tokens=False)
        right = parse_calls(answer) == expected(row)
        for key in (f"source {row['source']}", f"intent {row['intent']}", "all"):
            hit, count = totals.get(key, (0, 0))
            totals[key] = (hit + right, count + 1)
        if not right:
            misses.append((row, answer))
        if len(totals) and totals["all"][1] % 100 == 0:
            print(f"{totals['all'][1]}/{len(rows)} scored", flush=True)

    for key, (hit, count) in sorted(totals.items()):
        print(f"{key:24} {hit:5}/{count:<5} {100 * hit / count:5.1f}%")
    print(f"{1000 * seconds / max(1, len(rows)):.0f} ms per answer on {device}")
    for row, answer in misses[:args.show]:
        context = f"[{row['context']}] " if row.get("context") else ""
        print(f"\nMISS {context}{row['text']!r}\n  want {expected(row)}\n  got  {answer.strip()[:300]!r}")


if __name__ == "__main__":
    main()
