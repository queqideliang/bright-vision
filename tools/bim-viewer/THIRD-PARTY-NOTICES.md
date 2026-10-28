# Third-party notices

This viewer uses the following open-source components. Their names, copyrights,
and licenses remain with their respective authors. Branding this viewer does not
change ownership of these components.

| Component | Purpose | License | Upstream |
| --- | --- | --- | --- |
| Three.js | WebGL rendering and OrbitControls | MIT | https://github.com/mrdoob/three.js |
| fflate | Local ZIP decoding | MIT | https://github.com/101arrowz/fflate |
| Vite | Web build and module-preload runtime | MIT | https://github.com/vitejs/vite |
| Electron | Packaged Windows runtime | MIT, with Chromium and other third-party notices | https://github.com/electron/electron |
| electron-builder | Development and Windows packaging | MIT | https://github.com/electron-userland/electron-builder |
| TypeScript | Development and type checking | Apache-2.0 | https://github.com/microsoft/TypeScript |
| Playwright | Development browser verification | Apache-2.0 | https://github.com/microsoft/playwright |

Exact installed versions are pinned in `package-lock.json`. The build's
`scripts/copy-notices.mjs` copies the original Three.js, fflate, and Vite license
files from installed packages into `dist/licenses/`, together with a version
manifest. Distribute that directory with the Web build. Do not replace the
original license files with this summary.

Electron packaging includes Electron's `LICENSE`, `LICENSES.chromium.html`, and
the other standard runtime license materials. Keep those files with Windows
directory builds and installers. Electron's runtime may contain components under
additional licenses; the bundled Chromium notices are the applicable detailed
list.

This viewer does not bundle proprietary authoring
applications, their SDK binaries, third-party export plugins, account settings,
or user model files. This viewer has no integration that starts an external authoring application.

The licensing of company-authored viewer code and company branding should be
selected separately before a public source release. No third-party proprietary
binary is relicensed by this document.
