#!/usr/bin/env bash
#
# Drives the running stack the way a customer would:
#
#   1. register an endpoint pointing at the demo receiver
#   2. publish an event
#   3. wait for the worker to deliver it
#   4. assert the delivery is recorded as Delivered
#   5. confirm the idempotency key prevents duplicate events
#
# Usage: ./scripts/smoke-test.sh [api_url] [receiver_url_as_seen_by_the_api]

set -uo pipefail

API="${1:-http://localhost:8080}"
RECEIVER_INTERNAL="${2:-http://receiver:8080}"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

# jq is present on CI runners but not on every developer machine. Python is the
# fallback so this script runs in both places without extra setup.
if command -v jq >/dev/null 2>&1; then
    json() { jq -r "$1"; }
elif command -v python3 >/dev/null 2>&1 || command -v python >/dev/null 2>&1; then
    PYTHON="$(command -v python3 || command -v python)"

    json() {
        "${PYTHON}" -c '
import json, sys

path = sys.argv[1].lstrip(".")
value = json.load(sys.stdin)

for part in [p for p in path.replace("[", ".").replace("]", "").split(".") if p]:
    if value is None:
        break
    value = value[int(part)] if part.isdigit() else value.get(part)

if value is None:
    print("")
elif isinstance(value, bool):
    print(str(value).lower())
elif isinstance(value, (dict, list)):
    print(json.dumps(value))
else:
    print(value)
' "$1"
    }
else
    fail "Either jq or python is required."
fi

echo "1. Registering an endpoint"

endpoint_response="$(curl -fsS -X POST "${API}/api/endpoints" \
    -H 'Content-Type: application/json' \
    -d "{
        \"name\": \"Smoke test receiver\",
        \"url\": \"${RECEIVER_INTERNAL}\",
        \"subscribedEvents\": [\"order.*\"]
    }")" || fail "Could not register the endpoint."

endpoint_id="$(echo "${endpoint_response}" | json '.endpoint.id')"
secret="$(echo "${endpoint_response}" | json '.secret')"

[ -n "${endpoint_id}" ] || fail "No endpoint id returned."
case "${secret}" in
    whsec_*) ;;
    *) fail "No signing secret returned." ;;
esac

echo "   endpoint ${endpoint_id}"

echo "2. Publishing an event"

publish_response="$(curl -fsS -X POST "${API}/api/events" \
    -H 'Content-Type: application/json' \
    -H "Idempotency-Key: smoke-$(date +%s)" \
    -d '{
        "eventType": "order.created",
        "data": { "orderId": 1234, "total": 199.90 }
    }')" || fail "Could not publish the event."

deliveries_created="$(echo "${publish_response}" | json '.deliveriesCreated')"
event_id="$(echo "${publish_response}" | json '.eventId')"

[ "${deliveries_created}" = "1" ] || fail "Expected 1 delivery, got '${deliveries_created}'."

echo "   event ${event_id}, ${deliveries_created} delivery created"

echo "3. Waiting for the worker to deliver it"

status=""
delivery=""

for _ in $(seq 1 30); do
    deliveries="$(curl -fsS "${API}/api/deliveries?endpointId=${endpoint_id}")" || true
    status="$(echo "${deliveries}" | json '.items[0].status')"

    if [ "${status}" = "Delivered" ]; then
        delivery="${deliveries}"
        break
    fi

    if [ "${status}" = "Failed" ]; then
        echo "${deliveries}"
        fail "The delivery failed: $(echo "${deliveries}" | json '.items[0].lastError')"
    fi

    sleep 2
done

[ "${status}" = "Delivered" ] || fail "Delivery did not complete in time, last status: '${status:-none}'."

echo "4. Verifying what was recorded"

status_code="$(echo "${delivery}" | json '.items[0].lastStatusCode')"
attempt_status="$(echo "${delivery}" | json '.items[0].attempts[0].statusCode')"

[ "${status_code}" = "200" ] || fail "Expected status 200, got '${status_code}'."
[ "${attempt_status}" = "200" ] || fail "The attempt log did not record the response."

echo "   delivered with HTTP ${status_code}, attempt history recorded"

echo "5. Checking the idempotency key"

key="smoke-idem-$(date +%s)"
payload='{"eventType":"order.updated","data":{"orderId":1}}'

first="$(curl -fsS -X POST "${API}/api/events" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: ${key}" -d "${payload}")"

second="$(curl -fsS -X POST "${API}/api/events" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: ${key}" -d "${payload}")"

first_id="$(echo "${first}" | json '.eventId')"
second_id="$(echo "${second}" | json '.eventId')"
was_duplicate="$(echo "${second}" | json '.wasDuplicate')"

[ "${first_id}" = "${second_id}" ] || fail "The idempotency key created two events."
[ "${was_duplicate}" = "true" ] || fail "The repeat was not reported as a duplicate."

echo "   the repeated publish returned the original event"

echo
echo "Smoke test passed."
