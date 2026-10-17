#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <ISSUE_KEY> [OUTPUT_JSON_FILE]" >&2
  exit 1
fi

ISSUE_KEY="$1"
if [[ ! "${ISSUE_KEY}" =~ ^[A-Z][A-Z0-9_]*-[0-9]+$ ]]; then
  echo "Invalid ISSUE_KEY format: ${ISSUE_KEY} (expected e.g. ABC-123)" >&2
  exit 1
fi
OUTPUT_FILE="${2:-}"

# Running in cloud contexts (Actions/Codespaces) may expose secrets via alternate env names.
is_cloud_run="false"
if [[ -n "${GITHUB_ACTIONS:-}" || -n "${CODESPACES:-}" || -n "${GITHUB_SERVER_URL:-}" ]]; then
  is_cloud_run="true"
fi

resolve_jira_var() {
  local primary_var="$1"
  if [[ -n "${!primary_var:-}" ]]; then
    return 0
  fi

  if [[ "${is_cloud_run}" != "true" ]]; then
    return 1
  fi

  local cloud_candidates=(
    "GITHUB_SECRET_${primary_var}"
    "GH_SECRET_${primary_var}"
    "${primary_var}_SECRET"
  )

  local candidate
  for candidate in "${cloud_candidates[@]}"; do
    if [[ -n "${!candidate:-}" ]]; then
      export "${primary_var}=${!candidate}"
      return 0
    fi
  done

  return 1
}

required_vars=(JIRA_BASE_URL JIRA_EMAIL JIRA_API_TOKEN)
for var_name in "${required_vars[@]}"; do
  if ! resolve_jira_var "${var_name}"; then
    echo "Missing required Jira credential: ${var_name}" >&2
    if [[ "${is_cloud_run}" == "true" ]]; then
      echo "In cloud runs, provide ${var_name} directly or via GITHUB_SECRET_${var_name}, GH_SECRET_${var_name}, or ${var_name}_SECRET." >&2
    fi
    exit 2
  fi
done

if [[ "${JIRA_BASE_URL}" != https://* ]]; then
  echo "JIRA_BASE_URL must use an https:// URL." >&2
  exit 1
fi
ISSUE_URL="${JIRA_BASE_URL%/}/rest/api/3/issue/${ISSUE_KEY}?fields=summary,description,issuetype,priority,status,labels,components,assignee,reporter,parent,subtasks,issuelinks"
COMMENT_URL="${JIRA_BASE_URL%/}/rest/api/3/issue/${ISSUE_KEY}/comment?maxResults=20"

issue_json="$(curl -sS -f -u "${JIRA_EMAIL}:${JIRA_API_TOKEN}" -H "Accept: application/json" "${ISSUE_URL}")"
start_at=0
comment_total=0
all_comments='[]'

while :; do
  comment_page_json="$(curl -sS -f -u "${JIRA_EMAIL}:${JIRA_API_TOKEN}" -H "Accept: application/json" "${COMMENT_URL}&startAt=${start_at}")"
  comment_total="$(jq -r '.total // 0' <<< "${comment_page_json}")"
  page_comments="$(jq -c '.comments // []' <<< "${comment_page_json}")"
  all_comments="$(jq -cn --argjson all "${all_comments}" --argjson page "${page_comments}" '$all + $page')"
  fetched_count="$(jq -r '.comments | length' <<< "${comment_page_json}")"
  if (( fetched_count == 0 )); then
    break
  fi
  start_at=$((start_at + fetched_count))
  if (( start_at >= comment_total )); then
    break
  fi
done

comment_json="$(jq -cn --argjson comments "${all_comments}" --argjson total "${comment_total}" '{startAt: 0, maxResults: $total, total: $total, comments: $comments}')"

combined_json="$(cat <<EOF
{
  "issue": ${issue_json},
  "comments": ${comment_json}
}
EOF
)"

if [[ -n "${OUTPUT_FILE}" ]]; then
  printf '%s\n' "${combined_json}" > "${OUTPUT_FILE}"
  echo "Saved Jira payload to ${OUTPUT_FILE}" >&2
else
  printf '%s\n' "${combined_json}"
fi
