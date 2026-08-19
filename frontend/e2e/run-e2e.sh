#!/usr/bin/env bash
# unswarm frontend E2E suite — agent-browser
# Usage: ./e2e/run-e2e.sh [base-url]   (default http://localhost:5173)
# Requires: agent-browser CLI installed + dev server running.
# Exits non-zero on first failed assertion.

set -u
BASE_URL="${1:-http://localhost:5173}"
PASS=0
FAIL=0
FAILED_NAMES=()

say()  { printf '\n\033[1;36m== %s ==\033[0m\n' "$*"; }
ok()   { PASS=$((PASS+1)); printf '  \033[1;32mPASS\033[0m %s\n' "$*"; }
fail() { FAIL=$((FAIL+1)); FAILED_NAMES+=("$*"); printf '  \033[1;31mFAIL\033[0m %s\n' "$*"; }

# assert_visible <label> <selector-or-ref>
assert_visible() {
  local label="$1" sel="$2"
  if agent-browser is visible "$sel" >/dev/null 2>&1; then ok "$label"; else fail "$label (not visible: $sel)"; fi
}

# assert_text <label> <selector-or-ref> <expected-substring>
assert_text() {
  local label="$1" sel="$2" expected="$3"
  local got
  got=$(agent-browser get text "$sel" 2>/dev/null)
  if [[ "$got" == *"$expected"* ]]; then ok "$label"; else fail "$label (expected '$expected' in '$got')"; fi
}

# assert_theme_choice <label> <expected-choice>  (checks persisted localStorage choice)
assert_theme_choice() {
  local label="$1" expected="$2"
  local got
  got=$(agent-browser eval "localStorage.getItem('unswarm-theme')" 2>/dev/null | tr -d '"')
  if [[ "$got" == "$expected" ]]; then ok "$label"; else fail "$label (expected choice '$expected', got '$got')"; fi
}

# assert_console_clean <label>
assert_console_clean() {
  local label="$1"
  local errs
  errs=$(agent-browser console 2>/dev/null | grep -iE "error|uncaught|failed to load" | grep -v "Download the React DevTools" || true)
  if [[ -z "$errs" ]]; then ok "$label"; else fail "$label (console: $errs)"; fi
}

# --- Boot ---
say "Boot"
agent-browser open "$BASE_URL/" >/dev/null 2>&1
sleep 1.5
assert_text "App title" "h1" "Dashboard"
assert_visible "Sidebar nav" "nav"
assert_console_clean "No console errors on boot"

# --- Theme toggle ---
say "Theme toggle (light/dark/system cycle + persistence)"
agent-browser snapshot -i -c >/dev/null 2>&1
agent-browser find role button click --name "Switch theme" >/dev/null 2>&1
sleep 0.3
assert_theme_choice "Theme -> light" "light"
agent-browser find role button click --name "Switch theme" >/dev/null 2>&1
sleep 0.3
assert_theme_choice "Theme -> dark" "dark"
agent-browser find role button click --name "Switch theme" >/dev/null 2>&1
sleep 0.3
assert_theme_choice "Theme -> system" "system"
# persistence: reload keeps choice
agent-browser reload >/dev/null 2>&1
sleep 1.2
assert_theme_choice "Theme choice persisted after reload" "system"

# --- Navigation: all 6 routes ---
say "Route rendering"
for route in "/:Dashboard" "/models:Models" "/fleet:Fleet" "/queue:Queue" "/logs:Logs" "/settings:Settings"; do
  path="${route%%:*}"; heading="${route##*:}"
  agent-browser open "$BASE_URL$path" >/dev/null 2>&1
  sleep 1.2
  assert_text "Route $path renders" "h1" "$heading"
done

# --- 404 ---
say "404 page"
agent-browser open "$BASE_URL/nonexistent-xyz" >/dev/null 2>&1
sleep 1
assert_text "404 page renders" "main" "Page not found"

# --- Mobile drawer ---
say "Mobile drawer (390x844)"
agent-browser set viewport 390 844 >/dev/null 2>&1
agent-browser open "$BASE_URL/" >/dev/null 2>&1
sleep 1.2
agent-browser find role button click --name "Open navigation" >/dev/null 2>&1
sleep 0.5
assert_visible "Drawer opens with nav links" "nav a[href='/models']"
agent-browser press Escape >/dev/null 2>&1
sleep 0.6
drawer_open=$(agent-browser eval "document.querySelector('.fixed.inset-y-0.left-0') !== null" 2>/dev/null)
if [[ "$drawer_open" == "false" ]]; then
  ok "Drawer closes on Escape"
else
  fail "Drawer closes on Escape (drawer still present: $drawer_open)"
fi
agent-browser set viewport 1440 900 >/dev/null 2>&1

# --- Panel flows ---
say "Models: register a model"
agent-browser open "$BASE_URL/models" >/dev/null 2>&1
sleep 1.2
agent-browser find role button click --name "Register" >/dev/null 2>&1
sleep 0.5
agent-browser find placeholder "my-model-7b" fill "e2e-test-model" >/dev/null 2>&1
agent-browser find placeholder "org/image:tag" fill "e2e/test:latest" >/dev/null 2>&1
# The submit button is the LAST "Register" button in the DOM (form renders after the toggle)
submit_ref=$(agent-browser snapshot -i -c 2>/dev/null | grep 'button "Register"' | tail -1 | grep -o 'ref=e[0-9]*' | head -1 | sed 's/ref=/@/')
if [[ -n "$submit_ref" ]]; then
  agent-browser click "$submit_ref" >/dev/null 2>&1
  sleep 1.5
  assert_text "Registered model appears in list" "main" "e2e-test-model"
else
  fail "Register submit button not found"
fi

say "Fleet: start a container"
agent-browser open "$BASE_URL/fleet" >/dev/null 2>&1
sleep 1.2
agent-browser find role button click --name "Start" >/dev/null 2>&1
sleep 1.5
assert_console_clean "Fleet start triggers no console errors"

say "Logs: filter by level"
agent-browser open "$BASE_URL/logs" >/dev/null 2>&1
sleep 1.2
agent-browser find label "Level" select "error" >/dev/null 2>&1 || agent-browser select "select" "error" >/dev/null 2>&1
sleep 0.5
assert_console_clean "Logs filter triggers no console errors"

say "Settings: toggle a policy switch"
agent-browser open "$BASE_URL/settings" >/dev/null 2>&1
sleep 1.2
agent-browser find role switch click >/dev/null 2>&1 || agent-browser click "button[role='switch']" >/dev/null 2>&1
sleep 0.5
assert_console_clean "Settings toggle triggers no console errors"

# --- Summary ---
say "Summary"
printf '\n\033[1;36m%d passed, %d failed\033[0m\n' "$PASS" "$FAIL"
if [[ $FAIL -gt 0 ]]; then
  printf 'Failed: %s\n' "${FAILED_NAMES[*]}"
  exit 1
fi
exit 0