# Golden .cbflow files

Flows that real versions of CodeBridge or its editors wrote, or that people write by hand. They are kept as they are, never edited to make a test pass:
the tests check that **this version still reads every one of them** (`manifest.json` says what each must do). If a change breaks one, the change is
what needs fixing: people have files like these on their disks.

- `released/`: byte for byte what shipped. The Visual Studio examples are the same in 0.5.4, 0.6.0 and 0.7.0 (the format has not changed since), and the
  greenhouse is the first flow of the next release that uses sensor and module blocks. Names start with the first version that had the file.
- `variants/`: the same flows spelled the other ways they turn up: the browser editor's camelCase, a byte order mark with CRLF, a hand-written minimal file,
  numbers as text or with a decimal point, and a file from a newer version with fields and a block this version does not know.
- `manifest.json`: for each file, its name, board, number of nodes and wires, whether it validates and runs, and the commands the simulated board must get.
- `GoldenFlows.cs`: shared by the Core, Visual Studio and web editor tests.

To add a file: put it here (a new folder per release, never replace an old file) and add its entry to `manifest.json`. A test fails if a file and the
manifest disagree.

The block catalogs of each release are in `catalogs.json`, and a test makes sure no later version removes or changes anything an older file relies on.
