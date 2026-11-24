#!/usr/bin/env bash
# Runs all three suites in the order that leaves the app in a good state.
#
#   1. unit tests   - pure logic, no network, no server
#   2. API tests    - drives the live server through every endpoint
#   3. UI tests     - renders the real React bundle in jsdom and taps through it
#
# The API suite empties the portfolio and the UI suite re-seeds it, so running
# them in this order always finishes with the six-holding demo portfolio in
# place rather than whatever the last test happened to leave behind.
set -uo pipefail
cd "$(dirname "$0")"
source ./env.sh

SERVER="${SERVER:-http://127.0.0.1:5000}"
rc=0

echo "════════ 1/3  unit tests ════════"
dotnet test --nologo 2>&1 | tail -4 || rc=1

echo
echo "════════ 2/3  API tests (needs the server on $SERVER) ════════"
if curl -sf "$SERVER/api/health" >/dev/null; then
  python3 tests/api_e2e.py | tail -25 || rc=1
else
  echo "server not reachable at $SERVER - start it first:"
  echo "  dotnet run --project src/Server"
  rc=1
fi

echo
echo "════════ 3/3  UI tests (jsdom, real bundle, real API) ════════"
if curl -sf "$SERVER/api/health" >/dev/null; then
  (cd src/Client && npm run --silent test:ui -- "$SERVER") | grep -E "^\[FAIL|PASSED|FAILURES|console errors" || rc=1
else
  echo "server not reachable at $SERVER"
  rc=1
fi

echo
if [ "$rc" -eq 0 ]; then echo "ALL SUITES PASSED"; else echo "SOMETHING FAILED"; fi
exit $rc
