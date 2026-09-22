"""Release coverage requirements, not a claim of live observation support."""

import json
from pathlib import Path


REQUIRED_PROFILES = frozenset({
    "dynamodb-basic-crud",
    "s3-basic-object-crud",
    "secretsmanager-basic-lifecycle",
    "sqs-standard-messaging",
})
CERTIFICATION = Path(__file__).resolve().parents[1] / "docs/site/workload-ga.json"


def required_profiles() -> frozenset[str]:
    def unique_fields(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate certification field: {key}")
            result[key] = value
        return result

    try:
        rows = json.loads(CERTIFICATION.read_text(encoding="utf-8"), object_pairs_hook=unique_fields)
        if not isinstance(rows, list) or not rows:
            raise ValueError("certification must be a nonempty array")
        seen = set()
        ga = set()
        for row in rows:
            if not isinstance(row, dict) or type(row.get("schema_version")) is not int or row["schema_version"] != 1:
                raise ValueError("unsupported certification row")
            profile = row.get("profile_id")
            if not isinstance(profile, str) or not profile or profile in seen:
                raise ValueError("missing or duplicate certification profile")
            seen.add(profile)
            if type(row.get("profile_version")) is not int or row["profile_version"] <= 0:
                raise ValueError(f"invalid certification profile version for {profile}")
            if not isinstance(row.get("verdict"), str) or row["verdict"] not in ("ga", "candidate", "conditional", "blocked"):
                raise ValueError(f"unknown certification verdict for {profile}")
            if row["verdict"] == "ga":
                if profile in REQUIRED_PROFILES and row["profile_version"] != 1:
                    raise ValueError(f"new GA profile version requires explicit release coverage: {profile}")
                ga.add(profile)
        if not REQUIRED_PROFILES <= seen:
            raise ValueError("certification is missing a required release profile")
        if ga - REQUIRED_PROFILES:
            raise ValueError("new GA profiles require explicit release coverage: " + ", ".join(sorted(ga - REQUIRED_PROFILES)))
    except (OSError, UnicodeError, ValueError) as error:
        raise SystemExit(f"release-profile-coverage: {error}") from error
    # Expiry/downgrade must not silently shrink a release's evidence obligations.
    return REQUIRED_PROFILES
