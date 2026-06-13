"""
Upload the safety dataset and launch an OpenAI fine-tuning job for gpt-4o-mini.

Prereqs:
  - OPENAI_API_KEY set in the environment.
  - data/train.jsonl + data/val.jsonl built by build_safety_dataset.py.

Run:  python run_finetune.py            # default base + suffix
      python run_finetune.py --suffix docqa-safety-v2

On success it prints the fine-tuned model ID, e.g.
  ft:gpt-4o-mini-2024-07-18:org::AbC123
Set Rag__SimpleModel to that ID (ensure OpenRouter:ApiKey is empty) and restart
the API, then compare: python ../eval.py --safety-only
"""

import argparse
import os
import time

from openai import OpenAI

HERE = os.path.dirname(__file__)
DATA_DIR = os.path.join(HERE, "data")
BASE_MODEL = "gpt-4o-mini-2024-07-18"
POLL_SECONDS = 30


def upload(client: OpenAI, path: str) -> str:
    print(f"Uploading {os.path.basename(path)} ...")
    with open(path, "rb") as f:
        file_obj = client.files.create(file=f, purpose="fine-tune")
    print(f"  file id: {file_obj.id}")
    return file_obj.id


def main() -> None:
    parser = argparse.ArgumentParser(description="Launch a safety fine-tune job")
    parser.add_argument("--train", default=os.path.join(DATA_DIR, "train.jsonl"))
    parser.add_argument("--val", default=os.path.join(DATA_DIR, "val.jsonl"))
    parser.add_argument("--base", default=BASE_MODEL)
    parser.add_argument("--suffix", default="docqa-safety")
    args = parser.parse_args()

    if not os.getenv("OPENAI_API_KEY"):
        raise SystemExit("FATAL: OPENAI_API_KEY is not set.")
    for p in (args.train, args.val):
        if not os.path.exists(p):
            raise SystemExit(f"FATAL: {p} not found — run build_safety_dataset.py first.")

    client = OpenAI()

    train_id = upload(client, args.train)
    val_id = upload(client, args.val)

    print(f"Creating fine-tune job (base={args.base}, suffix={args.suffix}) ...")
    job = client.fine_tuning.jobs.create(
        training_file=train_id,
        validation_file=val_id,
        model=args.base,
        suffix=args.suffix,
    )
    print(f"  job id: {job.id}")

    print("Polling job status (Ctrl-C to stop watching; the job keeps running) ...")
    while True:
        job = client.fine_tuning.jobs.retrieve(job.id)
        trained = getattr(job, "trained_tokens", None)
        print(f"  status={job.status}  trained_tokens={trained}")
        if job.status == "succeeded":
            print(f"\nSUCCESS — fine-tuned model:\n  {job.fine_tuned_model}")
            print("\nNext: set Rag__SimpleModel to this ID, restart the API, then run")
            print("  python ../eval.py --safety-only   # compare before/after")
            return
        if job.status in ("failed", "cancelled"):
            err = getattr(job, "error", None)
            raise SystemExit(f"\nJob {job.status}: {err}")
        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
