# Safety fine-tuning

A small pipeline that fine-tunes `gpt-4o-mini` to be more robust on the three
safety axes the eval suite checks: **injection resistance** (direct, subtle, and
document-embedded), **refusal correctness** (out-of-domain questions), and **PII
non-leakage** — while keeping legitimate in-domain answers intact (the dataset is
~30-40% positive in-domain examples to prevent over-refusal).

No backend code changes are needed: the model ID flows from `Rag:SimpleModel`
straight into `OpenAIChatAdapter.BuildService(model)`, so a `ft:gpt-4o-mini:...`
ID just works (when `OpenRouter:ApiKey` is empty and `OpenAI:ApiKey` is set).

## Files

| File | Purpose |
|---|---|
| `build_safety_dataset.py` | Generates `data/train.jsonl` + `data/val.jsonl` (OpenAI chat format). System prompt is a verbatim copy of `TemplatePromptBuilder.SystemPrompt`. |
| `run_finetune.py` | Uploads the files, creates the job, polls, prints the resulting model ID. |
| `data/` | Generated JSONL (safe to gitignore). |

## Procedure

Use the eval venv (Python 3.11) so `openai` is available.

```powershell
cd eval\finetune

# 1. Build + spot-check the dataset
..\.venv\Scripts\python build_safety_dataset.py
#    Inspect data\train.jsonl — confirm refusals, PII declines, and grounded
#    answers look right. The system prompt MUST match TemplatePromptBuilder.cs.

# 2. Baseline the current model BEFORE training (cheap, no Ragas judge calls)
cd ..
.\.venv\Scripts\python eval.py --safety-only
#    Record the report-only rows (Injection subtle/indirect, Adversarial refusal).

# 3. Launch the fine-tune (~$3-8, ~20-60 min for ~160 examples)
cd finetune
..\.venv\Scripts\python run_finetune.py
#    Prints e.g.  ft:gpt-4o-mini-2024-07-18:org::AbC123

# 4. Deploy: point SimpleModel at the new ID and restart the API.
#    Either edit src/DocumentQA.Api/appsettings.json  ("Rag":{"SimpleModel": "ft:..."})
#    or set an env var on the api compose service:
#       Rag__SimpleModel=ft:gpt-4o-mini-2024-07-18:org::AbC123
#    Confirm OpenRouter:ApiKey is empty (otherwise calls route to OpenRouter,
#    which does not host your fine-tune).
docker compose up -d api    # if running via compose

# 5. Compare AFTER training
cd ..
.\.venv\Scripts\python eval.py --safety-only      # safety deltas
.\.venv\Scripts\python eval.py                    # full run: confirm no
#    faithfulness/relevancy regression and no over-refusal (refusal precision).
```

## Promoting report-only checks to hard-fail

Once the tuned model passes the subtle/indirect injection and adversarial-refusal
checks, move those names from `REPORT_ONLY` into `HARD_SAFETY` in `eval/eval.py`
so CI gates on them going forward.

## Notes & risks

- **Over-refusal** is the main regression risk. The dataset counterbalances with
  positive in-domain examples (FACTS + `golden_set.json`); the `Refusal precision`
  row in the eval summary is your guard — watch for in-domain questions that start
  refusing after deployment.
- **System-prompt drift**: `build_safety_dataset.py` embeds the system prompt as a
  constant. If `TemplatePromptBuilder.cs` changes, regenerate and retrain.
- **Cost/time**: a failed job (bad JSONL) still consumes time; `build_safety_dataset.py`
  validates role order and non-empty content before writing.
- **Augmentation**: drop a `feedback_examples.json` (positively-rated Q/A exported
  from the DB — see the SQL in `eval/eval.py`) next to `golden_set.json` to fold
  real production wins into the positive set.
