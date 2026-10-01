# Camera RAW regression fixtures

Each sample is redistributed under the Creative Commons CC0 1.0 Universal public-domain dedication, as individually indicated by the upstream raw.pixls.us repository. The samples are unmodified camera originals and are used only as decoder regression fixtures.

| Local file | Camera / upstream sample | Upstream | SHA-256 |
| --- | --- | --- | --- |
| `canon-eos-r6.cr3` | Canon EOS R6, 3:2 | [raw.pixls.us file 4659](https://raw.pixls.us/getfile.php/4659/nice/Canon%20-%20EOS%20R6%20-%203%3A2.CR3) | `74abb0a113d075ad9887a058082f40dd2a938c4813a08474d82356f11a027778` |
| `nikon-d2h.nef` | Nikon D2H, 12-bit lossy-compressed, 3:2 | [raw.pixls.us file 5227](https://raw.pixls.us/getfile.php/5227/nice/Nikon%20-%20D2H%20-%2012bit%2012bit%20compressed%20%28Lossy%20%28type%201%29%29%20%283%3A2%29.NEF) | `155edb938f884ea7372ce98d4ff5f965c3e413b43b95bc9923da6e92082cf914` |
| `sony-ilce-7s.arw` | Sony ILCE-7S, 14-bit compressed, 3:2 | [raw.pixls.us file 1581](https://raw.pixls.us/getfile.php/1581/nice/Sony%20-%20ILCE-7S%20-%2014bit%2014bit%20compressed%20%283%3A2%29.ARW) | `7cc338a0abc8fdad32d61006f1f8f412297b93e040c7efba572eb2bd8f8e8be2` |

The tests verify metadata dimensions, LibRaw decode, preview dimensions/aspect, JPEG encoding, and basic color/tonal variation. They do not claim a camera color-science golden match; per-camera demosaic and color-tuning references remain future work.
