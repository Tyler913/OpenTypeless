#!/bin/zsh
# Runs the TypelessCore test suite (swift-testing; works with the Command Line Tools toolchain).
set -e
cd "$(dirname "$0")/.."
swift test "$@"
