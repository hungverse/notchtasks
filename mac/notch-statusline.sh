#!/bin/bash
# Claude Code statusline: saves the statusline JSON (it carries the plan's rate_limits)
# for NotchTasks, then prints a short limit summary.
dir="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
input=$(cat)
tmp="$dir/notchtasks-status.json.tmp"
printf '%s' "$input" > "$tmp" && mv "$tmp" "$dir/notchtasks-status.json"
printf '%s' "$input" | /usr/bin/python3 -c '
import json, sys
rl = json.load(sys.stdin).get("rate_limits") or {}
parts = ["%s %d%%" % (n, rl[k]["used_percentage"]) for k, n in (("five_hour", "5h"), ("seven_day", "7d")) if k in rl]
print(" · ".join(parts))' 2>/dev/null
