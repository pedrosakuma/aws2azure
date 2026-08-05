#!/usr/bin/env bash
set -euo pipefail

readonly name_prefix="${NAME_PREFIX:-aws2azure-it-}"
readonly expected_purpose="${EXPECTED_PURPOSE_TAG:-aws2azure-it}"
readonly max_age_hours="${MAX_AGE_HOURS:-6}"
readonly aws_region="${AWS_REGION:-${AWS_DEFAULT_REGION:-}}"
readonly tag_lookup_attempts="${TAG_LOOKUP_ATTEMPTS:-4}"
readonly tag_lookup_retry_seconds="${TAG_LOOKUP_RETRY_SECONDS:-5}"

if [ -z "$aws_region" ]; then
  echo "::error::AWS_REGION (or AWS_DEFAULT_REGION) must be set." >&2
  exit 2
fi

readonly now_secs="$(date -u +%s)"
readonly max_age_secs=$(( max_age_hours * 3600 ))
readonly aws_account_id="$(aws sts get-caller-identity --query Account --output text)"
readonly minimum_epoch_secs=946684800
readonly maximum_name_epoch_secs=$(( now_secs + 300 ))

failed=0
last_inspection_failed=0

get_tags_json() {
  local arn="$1"
  local tags_json
  local attempt

  for attempt in $(seq 1 "$tag_lookup_attempts"); do
    if tags_json="$(aws resourcegroupstaggingapi get-resources \
      --resource-arn-list "$arn" \
      --query 'ResourceTagMappingList[0].Tags' \
      --output json 2>/dev/null)"; then
      if [ "$tags_json" != "null" ]; then
        printf '%s\n' "$tags_json"
        return 0
      fi
    elif [ "$attempt" -eq "$tag_lookup_attempts" ]; then
      echo "::error::Could not read tags for $arn via the Resource Groups Tagging API."
      return 1
    fi

    if [ "$attempt" -lt "$tag_lookup_attempts" ]; then
      sleep "$tag_lookup_retry_seconds"
    fi
  done

  printf 'null\n'
}

parse_created_secs_from_name() {
  local resource_name="$1"
  local suffix
  local candidate

  suffix="${resource_name#"$name_prefix"}"
  if [ "$suffix" = "$resource_name" ]; then
    return 1
  fi

  if [[ "$suffix" =~ ^([0-9]{10,})- ]]; then
    candidate="${BASH_REMATCH[1]}"
    if [ "$candidate" -lt "$minimum_epoch_secs" ] ||
       [ "$candidate" -gt "$maximum_name_epoch_secs" ]; then
      return 1
    fi

    printf '%s\n' "$candidate"
    return 0
  fi

  return 1
}

should_reap() {
  local label="$1"
  local arn="$2"
  local resource_name="$3"
  local tags_json
  local purpose
  local created
  local created_secs
  local age_secs
  local tag_lookup_failed
  local name_created_secs

  last_inspection_failed=0
  tag_lookup_failed=0
  if ! tags_json="$(get_tags_json "$arn")"; then
    tag_lookup_failed=1
    tags_json='null'
  fi
  purpose="$(jq -r '(. // []) | map(select(.Key == "purpose"))[0].Value // empty' <<< "$tags_json")"
  created="$(jq -r '(. // []) | map(select(.Key == "created"))[0].Value // empty' <<< "$tags_json")"

  if [ -z "$purpose" ]; then
    echo "::warning::$label has no 'purpose' tag. The reaper relies on the ${name_prefix} name prefix as the ownership backstop."
  elif [ "$purpose" != "$expected_purpose" ]; then
    echo "::warning::$label has purpose '$purpose' instead of '$expected_purpose'."
  fi

  created_secs=0
  if [ -n "$created" ]; then
    created_secs="$(date -u -d "$created" +%s 2>/dev/null || echo 0)"
    if [ "$created_secs" -eq 0 ]; then
      echo "::warning::$label has an unparseable 'created' tag ('$created')."
    fi
  fi

  if [ "$created_secs" -eq 0 ]; then
    if name_created_secs="$(parse_created_secs_from_name "$resource_name")"; then
      created_secs="$name_created_secs"
      echo "::notice::$label is using its ${name_prefix}<unix-epoch>-... name fallback for age checks."
    elif [ "$tag_lookup_failed" -ne 0 ]; then
      echo "::error::$label has no usable age signal because tag lookup failed and its name does not carry the documented ${name_prefix}<unix-epoch>-... fallback."
      last_inspection_failed=1
      return 1
    elif [ -z "$created" ]; then
      echo "::warning::$label has no 'created' tag and no parseable name timestamp — treating it as an orphan."
      return 0
    else
      echo "::warning::$label has no usable age signal after an unparseable 'created' tag and no parseable name timestamp — treating it as an orphan."
      return 0
    fi
  fi

  age_secs=$(( now_secs - created_secs ))
  if [ "$age_secs" -lt 0 ]; then
    echo "::warning::$label has a future 'created' tag ('$created'); keeping it to avoid premature cleanup."
    return 1
  fi

  if [ "$age_secs" -gt "$max_age_secs" ]; then
    echo "Reaping $label (age ${age_secs}s > ${max_age_secs}s)."
    return 0
  fi

  echo "Keeping $label (age ${age_secs}s <= ${max_age_secs}s — likely an active run)."
  return 1
}

delete_s3_bucket() {
  local bucket_name="$1"
  local versions_json
  local objects_json
  local uploads_json
  local payload
  local object_count
  local upload
  local upload_key
  local upload_id
  local batch_payload
  local error_output

  echo "Deleting S3 bucket $bucket_name."

  while :; do
    if ! versions_json="$(aws s3api list-object-versions --bucket "$bucket_name" --output json 2>&1)"; then
      if grep -q 'NoSuchBucket' <<< "$versions_json"; then
        echo "S3 bucket $bucket_name is already absent."
        return 0
      fi
      echo "::error::Could not list object versions for S3 bucket $bucket_name: $versions_json"
      return 1
    fi
    payload="$(jq -c '
      ((.Versions // []) | map({Key: .Key, VersionId: .VersionId})) +
      ((.DeleteMarkers // []) | map({Key: .Key, VersionId: .VersionId}))
    ' <<< "$versions_json")"
    object_count="$(jq 'length' <<< "$payload")"
    if [ "$object_count" -eq 0 ]; then
      break
    fi

    while IFS= read -r batch_payload; do
      [ -z "$batch_payload" ] && continue
      if ! error_output="$(aws s3api delete-objects \
        --bucket "$bucket_name" \
        --delete "$batch_payload" 2>&1 > /dev/null)"; then
        if grep -q 'NoSuchBucket' <<< "$error_output"; then
          echo "S3 bucket $bucket_name is already absent."
          return 0
        fi
        echo "::error::Could not delete versioned objects from S3 bucket $bucket_name: $error_output"
        return 1
      fi
    done < <(jq -c '
      . as $objects
      | range(0; ($objects | length); 1000)
      | {Objects: $objects[.: . + 1000], Quiet: true}
    ' <<< "$payload")
  done

  while :; do
    if ! objects_json="$(aws s3api list-objects-v2 --bucket "$bucket_name" --output json 2>&1)"; then
      if grep -q 'NoSuchBucket' <<< "$objects_json"; then
        echo "S3 bucket $bucket_name is already absent."
        return 0
      fi
      echo "::error::Could not list objects for S3 bucket $bucket_name: $objects_json"
      return 1
    fi
    payload="$(jq -c '(.Contents // []) | map({Key: .Key})' <<< "$objects_json")"
    object_count="$(jq 'length' <<< "$payload")"
    if [ "$object_count" -eq 0 ]; then
      break
    fi

    while IFS= read -r batch_payload; do
      [ -z "$batch_payload" ] && continue
      if ! error_output="$(aws s3api delete-objects \
        --bucket "$bucket_name" \
        --delete "$batch_payload" 2>&1 > /dev/null)"; then
        if grep -q 'NoSuchBucket' <<< "$error_output"; then
          echo "S3 bucket $bucket_name is already absent."
          return 0
        fi
        echo "::error::Could not delete objects from S3 bucket $bucket_name: $error_output"
        return 1
      fi
    done < <(jq -c '
      . as $objects
      | range(0; ($objects | length); 1000)
      | {Objects: $objects[.: . + 1000], Quiet: true}
    ' <<< "$payload")
  done

  while :; do
    if ! uploads_json="$(aws s3api list-multipart-uploads --bucket "$bucket_name" --output json 2>&1)"; then
      if grep -q 'NoSuchBucket' <<< "$uploads_json"; then
        echo "S3 bucket $bucket_name is already absent."
        return 0
      fi
      echo "::error::Could not list multipart uploads for S3 bucket $bucket_name: $uploads_json"
      return 1
    fi
    object_count="$(jq '(.Uploads // []) | length' <<< "$uploads_json")"
    if [ "$object_count" -eq 0 ]; then
      break
    fi

    while IFS= read -r upload; do
      [ -z "$upload" ] && continue
      upload_key="$(jq -r '.Key' <<< "$upload")"
      upload_id="$(jq -r '.UploadId' <<< "$upload")"
      if ! error_output="$(aws s3api abort-multipart-upload \
        --bucket "$bucket_name" \
        --key "$upload_key" \
        --upload-id "$upload_id" 2>&1 > /dev/null)"; then
        if grep -q 'NoSuchBucket' <<< "$error_output"; then
          echo "S3 bucket $bucket_name is already absent."
          return 0
        fi
        echo "::error::Could not abort multipart upload $upload_id in S3 bucket $bucket_name: $error_output"
        return 1
      fi
    done < <(jq -c '.Uploads[]?' <<< "$uploads_json")
  done

  if ! error_output="$(aws s3api delete-bucket --bucket "$bucket_name" 2>&1 > /dev/null)"; then
    if grep -q 'NoSuchBucket' <<< "$error_output"; then
      echo "S3 bucket $bucket_name is already absent."
      return 0
    fi
    echo "::error::Could not delete S3 bucket $bucket_name: $error_output"
    return 1
  fi
}

delete_dynamodb_table() {
  local table_name="$1"
  local error_output

  echo "Deleting DynamoDB table $table_name."
  if ! error_output="$(aws dynamodb delete-table --table-name "$table_name" 2>&1 > /dev/null)"; then
    if grep -q 'ResourceNotFoundException' <<< "$error_output"; then
      echo "DynamoDB table $table_name is already absent."
      return 0
    fi
    echo "::error::Could not delete DynamoDB table $table_name: $error_output"
    return 1
  fi
}

delete_kinesis_stream() {
  local stream_name="$1"
  local error_output

  echo "Deleting Kinesis stream $stream_name."
  if ! error_output="$(aws kinesis delete-stream \
    --stream-name "$stream_name" \
    --enforce-consumer-deletion 2>&1 > /dev/null)"; then
    if grep -q 'ResourceNotFoundException' <<< "$error_output"; then
      echo "Kinesis stream $stream_name is already absent."
      return 0
    fi
    echo "::error::Could not delete Kinesis stream $stream_name: $error_output"
    return 1
  fi
}

delete_sns_topic() {
  local topic_arn="$1"
  local error_output

  echo "Deleting SNS topic $topic_arn."
  if ! error_output="$(aws sns delete-topic --topic-arn "$topic_arn" 2>&1 > /dev/null)"; then
    if grep -Eq 'NotFound|InvalidParameter' <<< "$error_output"; then
      echo "SNS topic $topic_arn is already absent."
      return 0
    fi
    echo "::error::Could not delete SNS topic $topic_arn: $error_output"
    return 1
  fi
}

delete_sqs_queue() {
  local queue_url="$1"
  local error_output

  echo "Deleting SQS queue $queue_url."
  if ! error_output="$(aws sqs delete-queue --queue-url "$queue_url" 2>&1 > /dev/null)"; then
    if grep -q 'NonExistentQueue' <<< "$error_output"; then
      echo "SQS queue $queue_url is already absent."
      return 0
    fi
    echo "::error::Could not delete SQS queue $queue_url: $error_output"
    return 1
  fi
}

reap_s3_buckets() {
  local buckets_json
  local bucket_name
  local bucket_arn

  buckets_json="$(aws s3api list-buckets --output json)"
  while IFS= read -r bucket_name; do
    [ -z "$bucket_name" ] && continue
    bucket_arn="arn:aws:s3:::${bucket_name}"
    if should_reap "S3 bucket $bucket_name" "$bucket_arn" "$bucket_name"; then
      if ! delete_s3_bucket "$bucket_name"; then
        echo "::error::Failed to delete S3 bucket $bucket_name."
        failed=1
      fi
    elif [ "$last_inspection_failed" -ne 0 ]; then
      failed=1
    fi
  done < <(jq -r --arg prefix "$name_prefix" '.Buckets[]?.Name | select(startswith($prefix))' <<< "$buckets_json")
}

reap_dynamodb_tables() {
  local tables_json
  local table_name
  local table_arn

  tables_json="$(aws dynamodb list-tables --output json)"
  while IFS= read -r table_name; do
    [ -z "$table_name" ] && continue
    table_arn="arn:aws:dynamodb:${aws_region}:${aws_account_id}:table/${table_name}"
    if should_reap "DynamoDB table $table_name" "$table_arn" "$table_name"; then
      if ! delete_dynamodb_table "$table_name"; then
        echo "::error::Failed to delete DynamoDB table $table_name."
        failed=1
      fi
    elif [ "$last_inspection_failed" -ne 0 ]; then
      failed=1
    fi
  done < <(jq -r --arg prefix "$name_prefix" '.TableNames[]? | select(startswith($prefix))' <<< "$tables_json")
}

reap_kinesis_streams() {
  local streams_json
  local stream_name
  local stream_arn

  streams_json="$(aws kinesis list-streams --output json)"
  while IFS= read -r stream_name; do
    [ -z "$stream_name" ] && continue
    stream_arn="arn:aws:kinesis:${aws_region}:${aws_account_id}:stream/${stream_name}"
    if should_reap "Kinesis stream $stream_name" "$stream_arn" "$stream_name"; then
      if ! delete_kinesis_stream "$stream_name"; then
        echo "::error::Failed to delete Kinesis stream $stream_name."
        failed=1
      fi
    elif [ "$last_inspection_failed" -ne 0 ]; then
      failed=1
    fi
  done < <(jq -r --arg prefix "$name_prefix" '.StreamNames[]? | select(startswith($prefix))' <<< "$streams_json")
}

reap_sns_topics() {
  local topics_json
  local topic_arn
  local topic_name

  topics_json="$(aws sns list-topics --output json)"
  while IFS= read -r topic_arn; do
    [ -z "$topic_arn" ] && continue
    topic_name="${topic_arn##*:}"
    if ! [[ "$topic_name" == "$name_prefix"* ]]; then
      continue
    fi

    if should_reap "SNS topic $topic_name" "$topic_arn" "$topic_name"; then
      if ! delete_sns_topic "$topic_arn"; then
        echo "::error::Failed to delete SNS topic $topic_arn."
        failed=1
      fi
    elif [ "$last_inspection_failed" -ne 0 ]; then
      failed=1
    fi
  done < <(jq -r '.Topics[]?.TopicArn' <<< "$topics_json")
}

reap_sqs_queues() {
  local queues_json
  local queue_url
  local queue_name
  local queue_arn

  queues_json="$(aws sqs list-queues --queue-name-prefix "$name_prefix" --output json)"
  while IFS= read -r queue_url; do
    [ -z "$queue_url" ] && continue
    queue_name="${queue_url##*/}"
    queue_arn="arn:aws:sqs:${aws_region}:${aws_account_id}:${queue_name}"
    if should_reap "SQS queue $queue_name" "$queue_arn" "$queue_name"; then
      if ! delete_sqs_queue "$queue_url"; then
        echo "::error::Failed to delete SQS queue $queue_url."
        failed=1
      fi
    elif [ "$last_inspection_failed" -ne 0 ]; then
      failed=1
    fi
  done < <(jq -r '.QueueUrls[]?' <<< "$queues_json")
}

reap_s3_buckets
reap_dynamodb_tables
reap_kinesis_streams
reap_sns_topics
reap_sqs_queues

if [ "$failed" -eq 0 ]; then
  echo "No AWS cleanup errors detected."
fi

exit "$failed"
