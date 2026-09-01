# NoiseShader upstream record

The files in this directory named `*.hlsl` and the `LICENSE` file are copied unmodified from
[keijiro/NoiseShader](https://github.com/keijiro/NoiseShader) at
commit [`550100d4a74de1ba90eb1b8e90f25f9dbeec28d2`](https://github.com/keijiro/NoiseShader/tree/550100d4a74de1ba90eb1b8e90f25f9dbeec28d2).

Source paths:

- `Packages/jp.keijiro.noiseshader/Shader/*.hlsl`
- repository-root `LICENSE`

The matching Unity `.meta` files are CoreAI-owned metadata with project-local GUIDs and
`ShaderIncludeImporter` declarations. They intentionally are not copied from upstream, avoiding GUID
collisions if the original UPM package is installed alongside this vendored copy.

The upstream license is MIT. The adjacent `LICENSE` preserves the complete copyright and permission
notice verbatim, including the Ashima Arts and Stefan Gustavson attributions inherited from
`stegu/webgl-noise`.

`RbxNoiseShader.hlsl` in the parent directory is CoreAI-owned adaptation code. It keeps project-specific
names, include paths, and fixed-octave derivative accumulation out of this updateable upstream directory.

SHA-256 checksums of the vendored library sources:

| File | SHA-256 |
| --- | --- |
| `ClassicNoise2D.hlsl` | `b8dd33086fe80b8780225cc2a6fa8206423630ea280c6069191cf43ecad9e644` |
| `ClassicNoise3D.hlsl` | `32c3910c76599d4ce9bc18e015841e67f189aa4b11ccddbf7be6859c39f11978` |
| `Common.hlsl` | `f4d34c6fa5eaf4d1b9ec369de840232ba9798f6e099601176461221f2efa6e6d` |
| `Noise1D.hlsl` | `de8c079a6f7d36a6c317715860d50dc9f266aa2215cd4e026d49c4d3924f40dd` |
| `SimplexNoise2D.hlsl` | `8f72b4caabb0154df4d44279c5256c8371042aea9137c36bd3101b3aae2ee243` |
| `SimplexNoise3D.hlsl` | `003bd6f2366f432cfb40d8d212da2f012c424b2b737e3418bc9da397bd540c6a` |
| `LICENSE` | `bdafce1bb01517c9ae6c4f3620c01340790b5e9d039ae9e356347d1174250916` |
