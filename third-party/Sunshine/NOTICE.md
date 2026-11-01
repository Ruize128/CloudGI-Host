# Sunshine

CloudGI bundles unmodified runtime files from the official Windows lite distribution of Sunshine.

- Version: 2026.914.233613 (commit 63d35f702ee9e362e43263742981836ec0710384)
- Project: https://github.com/LizardByte/Sunshine
- Release: https://github.com/LizardByte/Sunshine/releases/tag/v2026.914.233613
- Source and build instructions: https://github.com/LizardByte/Sunshine/tree/v2026.914.233613
- License: GNU GPL version 3 (see LICENSE).

To obtain the exact upstream source including its pinned dependencies:

```sh
git clone --branch v2026.914.233613 --recurse-submodules https://github.com/LizardByte/Sunshine.git
```

Build downloads are cached under the Host project's obj directory. Runtime execution never downloads Sunshine.
Upstream assets and their notices are retained. Before public binary distribution, supply the corresponding source,
including required submodules and build material, using a method compliant with GPLv3 section 6.
CloudGI does not install Sunshine's system service or invoke its setup scripts.
