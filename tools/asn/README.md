# The offline network table (asn.dat)

`src/Windsock.Core/Data/asn.dat` maps every routed address to the autonomous
system announcing it - its AS number, registered name and country - so Windsock
can name the network behind an address (the provider behind a public address,
and later the organisation behind a connection) without asking anyone.

## Source and licence

[iptoasn.com](https://iptoasn.com/)'s combined IPv4 and IPv6 table,
`https://iptoasn.com/data/ip2asn-combined.tsv.gz`, rebuilt hourly from public
routing data. Licence: Public Domain Dedication and License (PDDL 1.0); no
attribution is required, and it is credited in THIRD-PARTY-NOTICES.md as a
courtesy.

## Rebuilding

From the repository root, with the .NET 10 SDK:

    curl -o %TEMP%\ip2asn-combined.tsv.gz https://iptoasn.com/data/ip2asn-combined.tsv.gz
    dotnet run tools/asn/BuildAsnTable.cs -- %TEMP%\ip2asn-combined.tsv.gz src/Windsock.Core/Data/asn.dat 2026-10-04

The date is the day the source was downloaded; it is stored in the file's
header. The tool prints the size, the row counts and a few lookups to check.
Rebuild before a release when the table is more than a few months old -
networks change hands, but slowly.

## Format

Little-endian, memory mapped and searched in place by `AsnTable`; written by
`AsnTableWriter`, both in `src/Windsock.Core/Asn`. The first 8 bytes are the
magic `WSCKASN1`; change the magic and every reader must change with it.

    0   "WSCKASN1"
    8   u32 data date as yyyymmdd
    12  u32 networks         16  u32 name bytes
    20  u32 IPv4 rows        24  u32 IPv6 rows
    28  u32 rows per index block (64)
    32  u32 offsets: networks, names, IPv4 index, IPv4 rows, IPv6 index, IPv6 rows
    56  u32 file length      60  u32 reserved
    networks  12 bytes each: u32 AS number, u32 name offset, u16 name length, 2 ASCII country
    names     UTF-8
    v4 index  8 bytes a block: u32 first start, u32 offset into the rows
    v6 index  12 bytes a block: u64 first start (top 64 bits), u32 offset
    rows      varint start - previous start (0 for a block's first), varint network index + 1 (0 = not routed)

Neighbouring ranges of the same network are joined, gaps become "not routed"
rows, and IPv6 is keyed on its top 64 bits (nothing longer than a /48 is routed
in practice). A lookup is a binary search over the block index and at most 64
varint pairs.
