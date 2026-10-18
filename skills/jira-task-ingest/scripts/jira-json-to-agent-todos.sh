#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <JIRA_JSON_FILE> [OUTPUT_JSON_FILE]" >&2
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "Missing dependency: jq" >&2
  echo "Install jq and retry." >&2
  exit 3
fi

INPUT_FILE="$1"
OUTPUT_FILE="${2:-}"

if [[ ! -f "${INPUT_FILE}" ]]; then
  echo "Input file not found: ${INPUT_FILE}" >&2
  exit 2
fi

issue_key="$(jq -r '.issue.key // "UNKNOWN-ISSUE"' "${INPUT_FILE}")"
summary="$(jq -r '.issue.fields.summary // "Jira issue"' "${INPUT_FILE}")"
priority="$(jq -r '.issue.fields.priority.name // "Unspecified"' "${INPUT_FILE}")"
status="$(jq -r '.issue.fields.status.name // "Unspecified"' "${INPUT_FILE}")"

subtasks_json="$(jq -c '[.issue.fields.subtasks[]? | {title: (.fields.summary // .key // "Review subtask")} ]' "${INPUT_FILE}")"
labels_text="$(jq -r '(.issue.fields.labels // []) | if length == 0 then "" else join(", ") end' "${INPUT_FILE}")"
links_count="$(jq -r '(.issue.fields.issuelinks // []) | length' "${INPUT_FILE}")"
comments_count="$(jq -r '.comments.total // ((.comments.comments // []) | length) // 0' "${INPUT_FILE}")"
description_excerpt="$(jq -r '
  def adf_text:
    if . == null then ""
    elif type == "string" then .
    elif type == "array" then map(adf_text) | join(" ")
    elif type == "object" then ((.text // "") + " " + ((.content // []) | adf_text))
    else "" end;
  (.issue.fields.description | adf_text | gsub("\\s+"; " ") | gsub("^\\s+|\\s+$"; "")) as $desc
  | if ($desc | length) > 100 then ($desc[0:97] + "...") else $desc end
' "${INPUT_FILE}")"
has_acceptance_criteria="$(jq -r '
  def adf_text:
    if . == null then ""
    elif type == "string" then .
    elif type == "array" then map(adf_text) | join(" ")
    elif type == "object" then ((.text // "") + " " + ((.content // []) | adf_text))
    else "" end;
  (.issue.fields.description | adf_text | ascii_downcase | test("acceptance\\s*criteria"))
' "${INPUT_FILE}")"

todos_json="$(jq -n \
  --arg issue_key "${issue_key}" \
  --arg summary "${summary}" \
  --arg priority "${priority}" \
  --arg status "${status}" \
  --arg labels_text "${labels_text}" \
  --arg description_excerpt "${description_excerpt}" \
  --argjson links_count "${links_count}" \
  --argjson comments_count "${comments_count}" \
  --argjson has_acceptance_criteria "${has_acceptance_criteria}" \
  --argjson subtasks "${subtasks_json}" '
  def base:
    [
      { id: 1, title: ("Review " + $issue_key + " context: " + $summary + " (Priority: " + $priority + ", Status: " + $status + ")"), status: "not-started" },
      { id: 2, title: ("Implement requirements from description: " + (if $description_excerpt == "" then "No description provided" else $description_excerpt end)), status: "not-started" },
      { id: 3, title: ("Handle labels and metadata: " + (if $labels_text == "" then "No labels" else $labels_text end)), status: "not-started" },
      { id: 4, title: ("Add or update tests"), status: "not-started" },
      { id: 5, title: (if $has_acceptance_criteria then "Validate acceptance criteria from issue description" else "Validate acceptance criteria with stakeholder (not explicitly listed)" end), status: "not-started" },
      { id: 6, title: ("Review linked Jira issues (" + ($links_count | tostring) + ")"), status: "not-started" },
      { id: 7, title: ("Review Jira comments for additional requirements (" + ($comments_count | tostring) + ")"), status: "not-started" }
    ];

  def subtask_items:
    $subtasks
    | to_entries
    | map({
        id: (8 + .key),
        title: ("Complete subtask: " + .value.title),
        status: "not-started"
      });

  base + subtask_items
')"

if [[ -n "${OUTPUT_FILE}" ]]; then
  printf '%s\n' "${todos_json}" > "${OUTPUT_FILE}"
  echo "Saved agent todo JSON to ${OUTPUT_FILE}" >&2
else
  printf '%s\n' "${todos_json}"
fi
