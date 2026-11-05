#!/bin/bash
# Sourceable bootstrap helper: only a validated archive may enter the cache.
ensure_jar() (
    if [ "$#" -ne 2 ] || [ -z "$1" ] || [ -z "$2" ]; then
        echo "ensure_jar requires a URL and destination." >&2
        return 1
    fi
    local url="$1" destination="$2" temporary attempt
    local connect_timeout="${IOS_JAR_CONNECT_TIMEOUT_SECONDS:-10}"
    local max_time="${IOS_JAR_MAX_TIME_SECONDS:-60}"
    case "$connect_timeout" in
        [1-9] | [12][0-9] | 30) ;;
        *) echo "JAR connection timeout must be an integer 1..30 without leading zeros." >&2; return 1 ;;
    esac
    case "$max_time" in
        [1-9] | [1-9][0-9] | 1[01][0-9] | 120) ;;
        *) echo "JAR transfer timeout must be an integer 1..120 without leading zeros." >&2; return 1 ;;
    esac
    if ! command -v jar >/dev/null 2>&1; then
        echo "JAR bootstrap validation requires the JDK jar command." >&2
        return 1
    fi
    if [ -f "$destination" ] && jar tf "$destination" >/dev/null 2>&1; then
        return 0
    fi
    if [ -e "$destination" ] && [ ! -f "$destination" ]; then
        echo "JAR destination is not a regular file: $destination" >&2
        return 1
    fi
    if ! command -v curl >/dev/null 2>&1; then
        echo "JAR bootstrap download requires curl." >&2
        return 1
    fi
    mkdir -p "$(dirname "$destination")" || return 1
    temporary=$(mktemp "$destination.download.XXXXXX") || return 1
    trap 'rm -f -- "$temporary"' EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    for attempt in 1 2 3; do
        if curl --fail --location --silent --show-error \
            --connect-timeout "$connect_timeout" --max-time "$max_time" \
            --output "$temporary" "$url" \
            && jar tf "$temporary" >/dev/null 2>&1; then
            # The temporary file is adjacent, so the final name never exposes a
            # partial transfer or HTTP error body. Keep any old cache on failure.
            mv -f -- "$temporary" "$destination" || return 1
            return 0
        fi
        echo "JAR download or archive validation failed ($attempt/3): $destination" >&2
        if [ "$attempt" -lt 3 ]; then sleep 1 || return 1; fi
    done
    return 1
)
