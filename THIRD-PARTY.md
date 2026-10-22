# Third-party software

SourceAFIS for .NET 3.14.0, upstream tag v3.14.0, https://github.com/robertvazan/sourceafis-net . Upstream README and Apache License 2.0 are preserved in thirdparty/SourceAFIS/UPSTREAM-README.md and LICENSE.

Local changes, 2026-10-22: FingerprintImage.cs retains only the upstream raw Gray8 constructor. The unused encoded image constructor and its ImageSharp imports/dependency were removed. Extraction/matching code is unchanged. The project targets .NET 10 and produces SourceAFIS.Raw.dll.

This build does not include or use ImageSharp. SourceAFIS.Raw depends on Dahomey.Cbor 1.16.1 (MIT), https://github.com/dahomey-technologies/Dahomey.Cbor .
