# AnitomySharp

C# parser files from https://github.com/chu-shen/AnitomySharp
at commit `579d94335dff23e3f44f72fa169ecadf03d039dc`.

AnitomySharp is a managed port of erengy/anitomy, derived from AnitomyJ.
The parser is used through its public Parse API for Bangumi and Trakt filenames.
No project-specific title or anime rules are added.

Local correction: the final tokenization range ends at the filename length;
its size excludes the already consumed prefix. This prevents empty tokens and
out-of-range episode-title parsing for filenames ending in a delimiter. Joining
single-character tokens also requires an existing unknown token on the left,
so a leading delimiter cannot access an index before the token list.

License: MPL-2.0. Original copyright headers are preserved in every source file.
The full license is in `THIRD_PARTY_LICENSES/AnitomySharp-MPL-2.0.txt`.
