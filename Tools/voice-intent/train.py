# /// script
# requires-python = ">=3.10"
# dependencies = ["torch", "transformers>=4.57", "accelerate", "huggingface_hub"]
# ///
"""Fine-tunes google/functiongemma-270m-it on the voice-intent data and pushes it to a private model repo.

On Hugging Face Jobs (billed to your HF credits; an A10G small is about $1/hour and this takes well under one):
  hf jobs uv run --flavor a10g-small --timeout 1h --secrets HF_TOKEN train.py \
      --data USER/doorway-voice-intents --out USER/doorway-functiongemma
Locally, with a GPU:
  uv run train.py --data data --out USER/doorway-functiongemma

Accept the Gemma licence on https://huggingface.co/google/functiongemma-270m-it first: the base model is gated.
Self-contained (Jobs only uploads this file): the data comes from --data, a local folder or a dataset repo that
make_dataset.py --push filled. Settings follow Google's FunctionGemma fine-tuning guide (constant 5e-5, eager
attention), with fewer epochs because there are far more rows. The loss is on the function calls only, not on
the prompt the app sends.
"""
import argparse
import json
import os

import torch
from huggingface_hub import HfApi, hf_hub_download
from transformers import AutoModelForCausalLM, AutoTokenizer, Trainer, TrainingArguments

BASE = "google/functiongemma-270m-it"


def data_file(data, name):
    if os.path.isdir(data):
        return os.path.join(data, name)
    return hf_hub_download(data, name, repo_type="dataset")


def read_rows(data, name):
    with open(data_file(data, name), encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def prompt_messages(row, developer):
    """What the app sends: the developer turn, what it last asked (if anything), what the user said."""
    messages = [{"role": "developer", "content": developer}]
    if row.get("context"):
        messages.append({"role": "assistant", "content": row["context"]})
    messages.append({"role": "user", "content": row["text"]})
    return messages


def answer_message(row):
    return {"role": "assistant", "tool_calls": [
        {"type": "function", "function": {"name": c["name"], "arguments": c["arguments"]}} for c in row["calls"]]}


def encode(rows, tokenizer, spec, max_length):
    """input_ids, and labels that are -100 over the prompt so only the answer is learnt."""
    examples, mismatched = [], 0
    for row in rows:
        messages = prompt_messages(row, spec["developer"])
        prompt = tokenizer.apply_chat_template(messages, tools=spec["tools"], add_generation_prompt=True,
                                               tokenize=False)
        full = tokenizer.apply_chat_template(messages + [answer_message(row)], tools=spec["tools"], tokenize=False)
        prompt_ids = tokenizer(prompt, add_special_tokens=False)["input_ids"]
        ids = tokenizer(full, add_special_tokens=False)["input_ids"][:max_length]
        keep = len(prompt_ids)
        if ids[:keep] != prompt_ids:
            mismatched += 1
            keep = next(i for i, (a, b) in enumerate(zip(ids, prompt_ids)) if a != b)
        examples.append({"input_ids": ids, "labels": [-100] * keep + ids[keep:]})
    if mismatched:
        print(f"Warning: {mismatched} rows' prompts didn't prefix their full text; masked the common part only.")
    return examples


def collate(pad_id):
    def pad(batch):
        width = max(len(e["input_ids"]) for e in batch)
        ids = [e["input_ids"] + [pad_id] * (width - len(e["input_ids"])) for e in batch]
        labels = [e["labels"] + [-100] * (width - len(e["labels"])) for e in batch]
        mask = [[1] * len(e["input_ids"]) + [0] * (width - len(e["input_ids"])) for e in batch]
        return {"input_ids": torch.tensor(ids), "labels": torch.tensor(labels), "attention_mask": torch.tensor(mask)}
    return pad


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", required=True, help="local data folder or dataset repo id")
    parser.add_argument("--out", required=True, help="model repo id to push to (private)")
    parser.add_argument("--epochs", type=float, default=3)
    # Gemma's 262k-word vocabulary makes the logits big: 2 at a time, 4 steps to a batch of 8, fits an A10G.
    parser.add_argument("--batch", type=int, default=2)
    parser.add_argument("--accumulate", type=int, default=4)
    parser.add_argument("--lr", type=float, default=5e-5)
    parser.add_argument("--max-length", type=int, default=1024)
    args = parser.parse_args()

    with open(data_file(args.data, "tools.json"), encoding="utf-8") as f:
        spec = json.load(f)
    tokenizer = AutoTokenizer.from_pretrained(BASE)
    train = encode(read_rows(args.data, "train.jsonl"), tokenizer, spec, args.max_length)
    test = encode(read_rows(args.data, "test.jsonl"), tokenizer, spec, args.max_length)
    longest = max(len(e["input_ids"]) for e in train)
    print(f"{len(train)} train, {len(test)} test examples; longest {longest} tokens")

    bf16 = torch.cuda.is_available() and torch.cuda.is_bf16_supported()
    model = AutoModelForCausalLM.from_pretrained(BASE, dtype=torch.bfloat16 if bf16 else torch.float32,
                                                 attn_implementation="eager")
    trainer = Trainer(
        model=model,
        args=TrainingArguments(
            output_dir="out", num_train_epochs=args.epochs, per_device_train_batch_size=args.batch,
            per_device_eval_batch_size=args.batch, gradient_accumulation_steps=args.accumulate,
            learning_rate=args.lr, lr_scheduler_type="constant",
            bf16=bf16, logging_steps=20, eval_strategy="epoch", save_strategy="no", report_to="none",
            remove_unused_columns=False),
        train_dataset=train,
        eval_dataset=test,
        data_collator=collate(tokenizer.pad_token_id),
    )
    trainer.train()

    model.save_pretrained("out/model")
    tokenizer.save_pretrained("out/model")
    with open("out/model/tools.json", "w", encoding="utf-8") as f:
        json.dump(spec, f, indent=1)   # the app sends exactly these tools
    api = HfApi()
    api.create_repo(args.out, private=True, exist_ok=True)
    api.upload_folder(repo_id=args.out, folder_path="out/model")
    print(f"Pushed to https://huggingface.co/{args.out}")


if __name__ == "__main__":
    main()
