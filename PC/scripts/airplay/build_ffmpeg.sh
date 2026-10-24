#!/usr/bin/env bash
set -euo pipefail
export PATH="/usr/bin:$PATH"
AIR_ROOT="${AIR_ROOT:-$(cd "$(dirname "$0")" && pwd)}"
export PATH="$AIR_ROOT/toolchain/prefix/ucrt64/bin:$AIR_ROOT/toolchain/prefix/usr/bin:/usr/bin"
export PKG_CONFIG_PATH="$AIR_ROOT/toolchain/prefix/ucrt64/lib/pkgconfig"
mkdir -p "$AIR_ROOT/build-ffmpeg"
cd "$AIR_ROOT/build-ffmpeg"
"$AIR_ROOT/sources/ffmpeg-9.0.2/configure" \
  --prefix="$AIR_ROOT/ffmpeg-prefix" --target-os=mingw32 --arch=x86_64 --cc=gcc \
  --disable-everything --disable-autodetect --disable-programs --disable-doc \
  --disable-debug --disable-network --disable-x86asm --disable-gpl --disable-nonfree \
  --enable-shared --disable-static --enable-avcodec --enable-avutil \
  --enable-avformat --enable-avfilter --enable-swscale --enable-swresample \
  --enable-decoder=h264 --enable-decoder=aac --enable-decoder=alac \
  --enable-parser=h264 --enable-parser=aac
make -j8
make install
