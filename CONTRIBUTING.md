# Contributing to BamCam

Thanks for your interest! Bug reports, ideas, translations and pull requests are welcome.

## Reporting bugs and wishes

Use the issue templates. For wrong toolpaths or G-code, a small drawing that shows the problem helps most.

## Building

You need the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/BamCam.App
```

See the README for packages and the project structure.

## Pull requests

- Keep a pull request to one topic and describe what it changes.
- `dotnet test` must pass; add tests for new behaviour. Changes to generated G-code need a test that
  compares with a known-good output.
- Code style: follow the surrounding code (C# with nullable reference types, file-scoped namespaces,
  short XML comments on public members). User interface texts are English and wrapped in `T("...")`
  or `F("...", args)`.
- Translations: add the German text to `src/BamCam.Core/Localization/lang/de.json`. The
  `LocalizationTests` report missing entries. New languages are new `<code>.json` files (see README).
- Plugins are often the better place for special functions; see `samples/BamCam.SamplePlugin`.

## Clean-room rule

BamCam reads and writes CamBam's `.cb` format but is an independent implementation. Do not contribute
code, decompiled parts, binaries or files copied from CamBam or its samples and libraries. Describing
CamBam's visible behaviour and comparing with G-code that CamBam produced from your own drawings is fine.

## License

By contributing you agree that your contribution is licensed under the MIT license of this project.
