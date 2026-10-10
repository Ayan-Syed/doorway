"""Adds looser, more natural wordings to data/*.jsonl, written by a large open model through Hugging Face
Inference Providers (billed to your HF credits). Run after make_dataset.py, before --push.

  HF_TOKEN=hf_... python paraphrase.py --limit 400        about 400 requests; prints the tokens used
  python paraphrase.py --limit 400 --model Qwen/Qwen2.5-72B-Instruct

Each paraphrase keeps its source row's labels, so it's only asked to change the wording, never the places or the
meaning. Paraphrases of test rows go to test (source "paraphrase"), so they never leak into training.
Needs: pip install huggingface_hub
"""
import argparse
import json
import os
import random
import re

from huggingface_hub import InferenceClient

from make_dataset import DATA_DIR, noisy

PROMPT = """A student said this to a voice assistant that gives walking directions inside a university building:
{context}"{text}"
It means: {meaning}.

Write {n} different ways a real person might say the same thing out loud: casual, sometimes rambling, sometimes
terse, sometimes polite. Keep every room number, door and place exactly as meant; don't add new places.
One per line. No numbering, no quotes, nothing else."""


def meaning(calls):
    parts = []
    for c in calls:
        a = c["arguments"]
        if c["name"] == "set_location":
            parts.append(f"they are at {a['place']}" + (f" (the {a['door']} door)" if "door" in a else "")
                         + (f", near {a['near']}" if "near" in a else ""))
        elif c["name"] == "set_destination":
            parts.append(f"they want to go to {a['place']}")
        else:
            parts.append(f"command: {a['action']}")
    return "; ".join(parts)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--limit", type=int, default=400, help="source rows to paraphrase (one request each)")
    parser.add_argument("--per-row", type=int, default=4)
    parser.add_argument("--model", default="meta-llama/Llama-3.3-70B-Instruct")
    parser.add_argument("--seed", type=int, default=11)
    args = parser.parse_args()

    rng = random.Random(args.seed)
    client = InferenceClient(provider="auto", api_key=os.environ.get("HF_TOKEN"))
    splits = {}
    for split in ("train", "test"):
        with open(os.path.join(DATA_DIR, f"{split}.jsonl"), encoding="utf-8") as f:
            splits[split] = [json.loads(line) for line in f]
    # Commands are short and already varied; the places are where wording matters.
    pool = [r for rows in splits.values() for r in rows if r["source"] == "template" and r["intent"] != "command"]
    sources = rng.sample(pool, min(args.limit, len(pool)))

    added = {"train": [], "test": []}
    seen = {(r["context"], r["text"]) for rows in splits.values() for r in rows}
    tokens_in = tokens_out = 0
    next_id = max(r["id"] for rows in splits.values() for r in rows) + 1
    for i, row in enumerate(sources, 1):
        context = f'(after the assistant asked: "{row["context"]}")\n' if row["context"] else ""
        try:
            reply = client.chat_completion(
                model=args.model, max_tokens=300, temperature=0.9,
                messages=[{"role": "user", "content": PROMPT.format(
                    context=context, text=row["text"], meaning=meaning(row["calls"]), n=args.per_row)}])
        except Exception as e:   # a provider hiccup costs one row, not the run
            print(f"  row {row['id']}: {e}")
            continue
        if reply.usage:
            tokens_in += reply.usage.prompt_tokens
            tokens_out += reply.usage.completion_tokens
        for line in (reply.choices[0].message.content or "").splitlines():
            text = noisy(re.sub(r"^\s*(\d+[.)]|[-*])\s*", "", line).strip().strip('"'), rng)
            if not text or len(text) > 200 or (row["context"], text) in seen:
                continue
            seen.add((row["context"], text))
            added[row["split"]].append(dict(row, id=next_id, source="paraphrase", text=text))
            next_id += 1
        if i % 25 == 0:
            print(f"{i}/{len(sources)} rows, {tokens_in} tokens in, {tokens_out} out", flush=True)

    for split, rows in added.items():
        with open(os.path.join(DATA_DIR, f"{split}.jsonl"), "a", encoding="utf-8") as f:
            for row in rows:
                f.write(json.dumps(row, ensure_ascii=False) + "\n")
    print(f"Added {len(added['train'])} train and {len(added['test'])} test paraphrases "
          f"({tokens_in} tokens in, {tokens_out} out; see your usage at https://huggingface.co/settings/billing).")


if __name__ == "__main__":
    main()
