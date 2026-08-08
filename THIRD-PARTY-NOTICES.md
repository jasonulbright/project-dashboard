# Third-party notices

Project Dashboard redistributes the binaries listed below in both release assets —
`ProjectDashboard-Setup-<version>.exe` and `ProjectDashboard-Portable-<version>.zip`.
The list is the complete set of third-party assemblies published by
`installer\build-portable.ps1` into `installer\payload`, which is exactly what the
installer packs.

Every redistributed component is licensed under the MIT License. Versions are the ones
resolved by `src/ProjectDashboard/ProjectDashboard.csproj` and recorded in the published
`ProjectDashboard.deps.json`.

The .NET 10 Desktop Runtime is a prerequisite installed separately by the user; it is not
redistributed here and is not covered by this file.

## Redistributed components

### CommunityToolkit.Mvvm 8.4.2

- Upstream: https://github.com/CommunityToolkit/dotnet
- License: MIT
- File: `CommunityToolkit.Mvvm.dll`

### WPF UI 4.2.0

- Upstream: https://github.com/lepoco/wpfui
- License: MIT
- Files: `Wpf.Ui.dll`, `Wpf.Ui.Abstractions.dll`, `Wpf.Ui.DependencyInjection.dll`
- Packages: `WPF-UI`, `WPF-UI.Abstractions`, `WPF-UI.DependencyInjection`

### Microsoft.Extensions.\* 10.0.5

- Upstream: https://github.com/dotnet/runtime (the package metadata names the build
  repository https://github.com/dotnet/dotnet, which sources these projects from
  dotnet/runtime)
- License: MIT
- Packages and files (one assembly per package, each at version 10.0.5):

| Package | File |
|---|---|
| Microsoft.Extensions.Configuration | `Microsoft.Extensions.Configuration.dll` |
| Microsoft.Extensions.Configuration.Abstractions | `Microsoft.Extensions.Configuration.Abstractions.dll` |
| Microsoft.Extensions.Configuration.Binder | `Microsoft.Extensions.Configuration.Binder.dll` |
| Microsoft.Extensions.Configuration.CommandLine | `Microsoft.Extensions.Configuration.CommandLine.dll` |
| Microsoft.Extensions.Configuration.EnvironmentVariables | `Microsoft.Extensions.Configuration.EnvironmentVariables.dll` |
| Microsoft.Extensions.Configuration.FileExtensions | `Microsoft.Extensions.Configuration.FileExtensions.dll` |
| Microsoft.Extensions.Configuration.Json | `Microsoft.Extensions.Configuration.Json.dll` |
| Microsoft.Extensions.Configuration.UserSecrets | `Microsoft.Extensions.Configuration.UserSecrets.dll` |
| Microsoft.Extensions.DependencyInjection | `Microsoft.Extensions.DependencyInjection.dll` |
| Microsoft.Extensions.DependencyInjection.Abstractions | `Microsoft.Extensions.DependencyInjection.Abstractions.dll` |
| Microsoft.Extensions.Diagnostics | `Microsoft.Extensions.Diagnostics.dll` |
| Microsoft.Extensions.Diagnostics.Abstractions | `Microsoft.Extensions.Diagnostics.Abstractions.dll` |
| Microsoft.Extensions.FileProviders.Abstractions | `Microsoft.Extensions.FileProviders.Abstractions.dll` |
| Microsoft.Extensions.FileProviders.Physical | `Microsoft.Extensions.FileProviders.Physical.dll` |
| Microsoft.Extensions.FileSystemGlobbing | `Microsoft.Extensions.FileSystemGlobbing.dll` |
| Microsoft.Extensions.Hosting | `Microsoft.Extensions.Hosting.dll` |
| Microsoft.Extensions.Hosting.Abstractions | `Microsoft.Extensions.Hosting.Abstractions.dll` |
| Microsoft.Extensions.Logging | `Microsoft.Extensions.Logging.dll` |
| Microsoft.Extensions.Logging.Abstractions | `Microsoft.Extensions.Logging.Abstractions.dll` |
| Microsoft.Extensions.Logging.Configuration | `Microsoft.Extensions.Logging.Configuration.dll` |
| Microsoft.Extensions.Logging.Console | `Microsoft.Extensions.Logging.Console.dll` |
| Microsoft.Extensions.Logging.Debug | `Microsoft.Extensions.Logging.Debug.dll` |
| Microsoft.Extensions.Logging.EventLog | `Microsoft.Extensions.Logging.EventLog.dll` |
| Microsoft.Extensions.Logging.EventSource | `Microsoft.Extensions.Logging.EventSource.dll` |
| Microsoft.Extensions.Options | `Microsoft.Extensions.Options.dll` |
| Microsoft.Extensions.Options.ConfigurationExtensions | `Microsoft.Extensions.Options.ConfigurationExtensions.dll` |
| Microsoft.Extensions.Primitives | `Microsoft.Extensions.Primitives.dll` |

## License texts

### MIT License — .NET Foundation and Contributors

Applies to CommunityToolkit.Mvvm and to every Microsoft.Extensions.\* package listed above.

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### MIT License — Leszek Pomianowski and WPF UI Contributors

Applies to `WPF-UI`, `WPF-UI.Abstractions`, and `WPF-UI.DependencyInjection`.

```
MIT License

Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors. https://lepo.co/

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Tools invoked, not redistributed

Project Dashboard runs `git.exe` and the GitHub CLI (`gh`) as subprocesses. Neither is
bundled, redistributed, or modified; both are installed and licensed independently by the
user.
