# Faces the conformance probe needs

`Inter-latin-wght.woff2` is the file Google Fonts serves a current Chrome for
`https://fonts.googleapis.com/css2?family=Inter:wght@400;700`: one variable file, latin subset,
answering both weights. Fetched on 2026-10-05 from
`https://fonts.gstatic.com/s/inter/v20/UcC73FwrK3iLTeHuS_nVMrMxCp50SjIa1ZL7.woff2`.

It is here because the matrix has a question only a variable face can ask - does `font-weight: 700`
on a `wght` axis draw bold? - and the corpus carries no variable face of its own. It is the same
file the packager fetches and carries for the 47 blocks that link the service, so the case asks
about what a package actually renders rather than a stand-in.

Inter is Copyright (c) 2016 The Inter Project Authors, licensed under the SIL Open Font License
1.1. The licence is beside the file as `Inter-OFL.txt`.
