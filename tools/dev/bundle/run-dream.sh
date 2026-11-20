#!/bin/sh
# Start Dream from inside the bundle so imgui.ini and other relative files land here.
cd "$(dirname "$0")" || exit 1
exec ./dream "$@"
