# gap-scan-tools

The Python scanners behind `RimworldMP_Gap_Analysis.md`. Run them again after a RimWorld or MP update.

The paths at the top of each script are hard-coded:

- `RW` = a decompiled RimWorld source tree
- `MP` = `RimworldMP/Source Codes/Source`

Update them for your machine before running.

| Script | What it does |
|---|---|
| `mpreg.py` | Pulls every MP sync registration and patch target out of the source into `mpreg.json`. |
| `inherit.py <Base,...>` | Builds the vanilla class hierarchy (`inherit.json`) and lists subclasses of the given bases that MP never mentions. |
| `gizmoscan.py "<method names>"` | Lists every lambda and local function in those vanilla methods, numbered the way the C# compiler numbers them, and marks which ones MP registers. Writes `gizmos_<mode>.json`. |
| `ordinals.py` | The numbering model. The default mode, `ORDMODE=hybrid_any`, uses source order for iterator methods and Roslyn's scope-by-scope order for everything else (object initializers count as scopes; local functions are hoisted). |
| `score.py <modes>` | Scores a numbering mode against the known-correct registrations in `truth.py`. |
| `candidates.py` | Lists lambdas that change game state without going through a synced call. Review the output by hand. |
| `allmethods.py <Base>` | Lists every lambda in every method of the given base's subclasses (used for the FloatMenuOptionProvider pass). |
| `hashscan.py` | Lists hashed collections keyed by reference types that don't override `GetHashCode`. Informational only. |

Typical run:

```
python3 mpreg.py && python3 inherit.py Window
python3 gizmoscan.py "$(cat names.txt)"   # names.txt = comma-separated method names
python3 candidates.py
```

The numbering model is a heuristic. Treat a mismatch as a pointer to check against the real DLL, not as proof.
