# Community plugins

Plugins written by BamCam users, shared here so that others can find and use them.

**Use at your own risk.** They are checked to build and to do what they say, but they are not tested like BamCam
itself. A plugin runs with the same rights as BamCam, and BamCam asks once before it loads one. Please report problems
as an [issue](https://github.com/mottobug/BamCam/issues) with the plugin's name in the title.

## Using a plugin

Every [BamCam release](https://github.com/mottobug/BamCam/releases) has a ZIP file per community plugin, named
`community-<Author>.<Name>-<version>.zip`. Unpack it into a folder of its own below the plugin folder
(**Tools ▸ Plugins ▸ Open Plugin Folder**) and restart BamCam.

## The plugins

| Plugin | Author | What it does |
|---|---|---|
| *(none yet)* | | |

## Adding or changing a plugin

See [Sharing a plugin](../../docs/plugins.md#sharing-a-plugin) for the steps, done entirely on the GitHub website.

The rules in short:

- **One folder per plugin**, named `<Author>.<Name>`, for example `GeoffRoy.Revolve`. Start from a copy of
  [`_Template`](_Template).
- **Source code only.** No DLLs or other built files: the build makes the plugin from the source, so everyone can see
  what it does.
- **A `README.md`** in the folder: what the plugin does, how to use it, the author, the version and the licence. Add a
  line for it to the table above.
- **A licence that lets others use and change it**, MIT like BamCam if you have no other wish.
- **Changes** come as a pull request, like the first version. Raise `<Version>` in the `.csproj` with each change.

A plugin that many people use and that has proved itself can later come with BamCam itself, with its author's consent.
