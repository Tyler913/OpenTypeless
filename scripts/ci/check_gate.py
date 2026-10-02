#!/usr/bin/env python3
"""Fail closed: required jobs must succeed; only path-filtered jobs may be skipped."""
import json
import os


def failures(needs):
    errors = []
    for name in ("changes", "quality"):
        if needs.get(name, {}).get("result") != "success":
            errors.append(f"{name} did not succeed")
    outputs = needs.get("changes", {}).get("outputs", {})
    for job, platform in (("macos", "macos"), ("windows", "windows"), ("pipeline", "windows")):
        selected = outputs.get(platform)
        result = needs.get(job, {}).get("result")
        if selected not in ("true", "false"):
            errors.append(f"missing or invalid change detection for {platform}")
        elif selected == "true" and result != "success":
            errors.append(f"required {job} result is {result}")
        elif selected == "false" and result not in ("success", "skipped"):
            errors.append(f"{job} result is {result}")
    return errors


if __name__ == "__main__":
    errors = failures(json.loads(os.environ["CI_NEEDS"]))
    if errors:
        raise SystemExit("CI gate failed:\n" + "\n".join(errors))
    print("All required checks succeeded; only unneeded platform builds were skipped.")
