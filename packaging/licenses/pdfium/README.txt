PDFium license texts

The PDFium library in PrinterHelper (pdfium.dll on Windows, libpdfium.dylib on the Mac) comes from the NuGet
packages bblanchon.PDFium.Win32 and bblanchon.PDFium.macOS 160.0.8165. pdfium-binaries built them from PDFium
branch chromium/8165:

  pdfium-binaries  https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F8165
                   commit 83c18af693945b735503665259b728ba7d409fd0
  PDFium           https://pdfium.googlesource.com/pdfium/+/1f02bc7d908ab301fa2a2babd87226a19311ff38

The packages carry no license files, so the texts are kept here. These are the files pdfium-binaries puts in the
licenses folder of its release archives (its steps/08-licenses.sh), made the same way from the same revisions.
Four more are added because that script skips them although their code is compiled into the library: brotli,
libyuv, libc++ and libc++abi (libc++abi on the Mac only). The script takes every library named in the build files,
so a few of its files (libpng, simdutf) may cover code that is not in the library itself.

The revisions are the ones PDFium's DEPS file names at that commit. Each file is copied unchanged, except where the
license is a comment in a source file: then only that comment is kept, cut out the way pdfium-binaries does it.

File                        From
pdfium.txt                  pdfium LICENSE, without the leading "//" (BSD-3-Clause, and the Apache License 2.0
                            for parts of PDFium)
agg23.txt                   pdfium third_party/agg23/agg_array.h, license comment (Anti-Grain Geometry 2.3)
freetype.txt                pdfium third_party/freetype/FTL.TXT (The FreeType Project License; Latin-1 text)
highway.txt                 pdfium third_party/highway/LICENSE
lcms.txt                    pdfium third_party/lcms/src/include/lcms2.h, license comment (Little CMS)
libopenjpeg.txt             pdfium third_party/libopenjpeg/src/openjpeg.c, license comment (OpenJPEG)
abseil.txt                  chromium/src/third_party/abseil-cpp@56722f9eb6f40fb1a140caffd36e8f1f61b57aaf LICENSE
brotli.txt                  chromium/src/third_party/brotli@803ac71664ad62af1de39ff55e63ebbe07006fc8 LICENSE
dragonbox-Apache2-LLVM.txt  external/github.com/jk-jeon/dragonbox@beeeef91cf6fef89a4d4ba5e95d47ca64ccb3a44
                            LICENSE-Apache2-LLVM
dragonbox-Boost.txt         the same, LICENSE-Boost
fast_float.txt              external/github.com/fastfloat/fast_float@42f254836ee416ffcf2f4e6576260003ac33d48c
                            LICENSE-MIT
harfbuzz.txt                external/github.com/harfbuzz/harfbuzz@ff36703b95013ec1d437e6038e1aa7c881275f4e COPYING
icu.txt                     chromium/deps/icu@a0ea93436c528ca316a6e698e483e258839101a5 LICENSE
libjpeg_turbo.txt           chromium/deps/libjpeg_turbo@640f254ad0fa03f6b1f29f89b7dd9366f2f6e533 LICENSE.md
libjpeg_turbo-ijg.txt       the same, README.ijg
libpng.txt                  chromium/src/third_party/libpng@0179a807421cf96731cdfa10008bba04fda243c1 LICENSE
libyuv.txt                  libyuv/libyuv@1e2e3a35993609da1d89ac170b907719d555f4b3 LICENSE
llvm-libc.txt               external/github.com/llvm/llvm-project/libc@d8f2045008e230f3c5f50095d918c11f1997e477
                            LICENSE.TXT
libcxx.txt                  external/github.com/llvm/llvm-project/libcxx@9ba7713d3a0f1e179904c984bb626748b15e57d6
                            LICENSE.TXT
libcxxabi.txt               external/github.com/llvm/llvm-project/libcxxabi@ce814c994055b107c631c310b1c668e3714c0704
                            LICENSE.TXT
simdutf.txt                 chromium/src/third_party/simdutf@f7356eed293f8208c40b3c1b344a50bd70971983 LICENSE
zlib.txt                    chromium/src/third_party/zlib@9f9f52c60a14e8e772fb561029ae0a121fef7609 zlib.h,
                            license comment
pdfium-binaries.txt         bblanchon/pdfium-binaries LICENSE (MIT; the NuGet packages state Apache-2.0)

"pdfium" is https://pdfium.googlesource.com/pdfium at the commit above; the other repositories are under
https://chromium.googlesource.com/.

When the PDFium packages change, take the revisions from DEPS of the new branch and make these files again the
same way.
