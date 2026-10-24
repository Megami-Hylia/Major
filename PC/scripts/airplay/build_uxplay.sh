#!/usr/bin/env bash
set -euo pipefail
export PATH="/usr/bin:$PATH"
AIR_ROOT="${AIR_ROOT:-$(cd "$(dirname "$0")" && pwd)}"
PREFIX="$AIR_ROOT/toolchain/prefix/ucrt64"
export PATH="$PREFIX/bin:/usr/bin"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
cmake -S "$AIR_ROOT/sources/UxPlay-15a6b87172eca38b0df4e615fbf1bba108734dde" \
  -B "$AIR_ROOT/build-uxplay" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DNO_MARCH_NATIVE=ON -DUSE_MDNS=1 -DUSE_DNS_SD=OFF \
  -DCMAKE_C_COMPILER="$PREFIX/bin/gcc.exe" -DCMAKE_CXX_COMPILER="$PREFIX/bin/g++.exe" \
  -DCMAKE_PREFIX_PATH="$PREFIX" -DPKG_CONFIG_EXECUTABLE="$PREFIX/bin/pkgconf.exe"
cmake --build "$AIR_ROOT/build-uxplay" --parallel 8
