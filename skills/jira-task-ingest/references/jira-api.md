# Jira API Notes

Use Atlassian Jira Cloud REST API v3.

## Authentication
Basic auth with email and API token:
- Username: Jira account email
- Password: Jira API token

## Useful Endpoints
- Issue details:
  - GET /rest/api/3/issue/{issueIdOrKey}
- Issue comments:
  - GET /rest/api/3/issue/{issueIdOrKey}/comment
- Search (JQL):
  - GET /rest/api/3/search

## Common Fields For Task Breakdown
- summary
- description
- issuetype
- priority
- status
- labels
- components
- subtasks
- issuelinks

## Notes
- Rich text description is returned in Atlassian Document Format (ADF).
- If ADF parsing is needed, summarize plain text blocks and bullet lists for implementation tasks.
