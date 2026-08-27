# Terraria protocol baseline provenance

Vega's packet-security work must distinguish the pinned Terraria runtime binary from the readable public decompile used for semantic comparison.

## Pinned runtime

The Phase 25 behavior audit records the Terraria 1.4.5.8 Windows dedicated-server binary as:

- size: `26,028,032` bytes;
- SHA-256: `D87E3FAF08637F6BE8882C63E7F11FB7E792B0230006309618473ECE0F863E1E`.

The original audit also recorded protocol `326`. That value came from the earlier binary-inspection session, but the repository does not currently contain a reproducible extraction artifact proving the value. It therefore must not be treated as independently verified evidence.

## Readable comparison source

The pinned readable comparison source is:

`JonataOliveiraa/Terraria1.4.5@0459d7b61e35f15354a40fb9d1422d84e9bb4f2c`

Its decompiled `MessageBuffer.GetData` connection handshake compares against `"Terraria" + 325`. Multiplicity also retains protocol-325 compatibility test naming. This source is useful for control-flow and packet-semantics analysis, but it cannot by itself prove the 1.4.5.8 wire revision.

## Rule

Until the exact pinned 1.4.5.8 binary is re-inspected with a reproducible command/tool and the result is checked into the audit evidence:

- treat `326` as **provisional**;
- treat the readable source as **protocol 325 evidence**;
- do not change packet layouts merely to make either number win;
- resolve any 1.4.5.7/readable-source versus 1.4.5.8 binary conflict against reproducible evidence from the pinned 1.4.5.8 binary;
- keep runtime capacity constants independent from an unverified protocol label.

A future baseline refresh should record the extraction command, the exact binary hash, the handshake/version result and any packet-layout deltas together so the protocol number cannot turn into undocumented folklore again.
