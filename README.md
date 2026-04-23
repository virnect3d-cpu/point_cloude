# My Package

Unity package installable via Git URL.

## Installation

### Via Unity Package Manager (Git URL)

1. Open **Window → Package Manager** in Unity.
2. Click the **+** button → **Add package from git URL...**
3. Enter:

```
https://github.com/<YOUR_USER>/<YOUR_REPO>.git
```

Or pin to a specific version/branch:

```
https://github.com/<YOUR_USER>/<YOUR_REPO>.git#1.0.0
```

### Via `manifest.json`

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.virnect.mypackage": "https://github.com/<YOUR_USER>/<YOUR_REPO>.git#1.0.0"
  }
}
```

## Usage

```csharp
using Virnect.MyPackage;

var example = new Example();
example.DoSomething();
```

## License

MIT
