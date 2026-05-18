#!/usr/bin/env python3
"""
Smoke harness that drives the aws2azure proxy via the real boto3 client.

This is intentionally a stand-alone script (not a pytest fixture) so it can be
invoked from any CI step that already has Python + boto3. It expects the proxy
to be reachable at PROXY_URL (default http://127.0.0.1:5099) and uses Azurite
well-known dev credentials.

Currently runs against Phase-0 stubs (asserts 501 NotImplemented). Once
Phase 1's PutObject lands (#8), the assertions flip to expect a successful
round trip through the proxy → Azurite Blob path.
"""

from __future__ import annotations
import os
import sys
import uuid

try:
    import boto3
    from botocore.exceptions import ClientError
except ImportError:
    print("boto3 not installed. `pip install boto3` and retry.", file=sys.stderr)
    sys.exit(2)

PROXY_URL = os.environ.get("PROXY_URL", "http://127.0.0.1:5099")
ACCESS_KEY = os.environ.get("AWS_ACCESS_KEY_ID", "DEVSTOREACCOUNT1")
SECRET_KEY = os.environ.get(
    "AWS_SECRET_ACCESS_KEY",
    "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
)
PHASE = os.environ.get("AWS2AZURE_PHASE", "0")


def main() -> int:
    s3 = boto3.client(
        "s3",
        endpoint_url=PROXY_URL,
        aws_access_key_id=ACCESS_KEY,
        aws_secret_access_key=SECRET_KEY,
        region_name="us-east-1",
    )
    bucket = "it-" + uuid.uuid4().hex[:8]
    key = "hello.txt"
    body = b"hello from boto3 via aws2azure\n"
    try:
        s3.put_object(Bucket=bucket, Key=key, Body=body)
    except ClientError as e:
        status = e.response.get("ResponseMetadata", {}).get("HTTPStatusCode")
        if PHASE == "0" and status == 501:
            print(f"[ok] phase-0 stub returned 501 as expected ({bucket}/{key})")
            return 0
        print(f"[fail] PutObject errored: {e}", file=sys.stderr)
        return 1

    if PHASE == "0":
        print("[fail] PutObject succeeded but Phase 0 expected 501 stub", file=sys.stderr)
        return 1

    got = s3.get_object(Bucket=bucket, Key=key)
    payload = got["Body"].read()
    if payload != body:
        print(f"[fail] round-trip mismatch: {payload!r}", file=sys.stderr)
        return 1
    print(f"[ok] boto3 round-trip succeeded for {bucket}/{key}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
