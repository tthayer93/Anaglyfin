#!/bin/sh
# Builds the Jellyfin plugin-repository manifest that lives on the `metadata` branch.
#
#   sh .ci/release-manifest.sh \
#     --meta           artifacts/meta.json \
#     --version        0.1.0 \
#     --source-url     https://github.com/<owner>/<repo>/releases/download/v0.1.0/Anaglyfin_0.1.0.zip \
#     --checksum       <lowercase 32-character MD5 hex of the zip> \
#     --changelog-file notes.md \
#     --out            manifest.json \
#     [--previous      previous-manifest.json] [--timestamp  2026-09-21T20:00:00Z]
#
# The document this writes is the only thing a Jellyfin server fetches on its own: an
# administrator pastes a URL into Dashboard -> Plugins -> Catalogs, the server GETs that file
# every time the catalogue is opened, and on install it downloads `sourceUrl` and compares the
# bytes to `checksum` before unpacking. Everything an installation depends on is written here,
# and nothing here is decoration.
#
# The keys are exactly the ones Jellyfin 12 declares, in camelCase, because the server reads
# this document case-sensitively and ignores a key it does not recognise: `SourceUrl` is not a
# wrong spelling, it is an absent field. `imageUrl` and the pre-10.8 fields (`checksumType`,
# `sha256`, `downloadUrl`, `runtime`, `targetplatform`) are deliberately not written - the last
# group is ignored by 12 and only misleads a reader about which digest the server checks.
#
# `checksum` is the plugin zip's MD5, lowercase and 32 characters wide. Jellyfin 12 hashes the
# download with MD5 and compares it to this field before extracting, so the SHA-256 the
# packaging job records in meta.json is the wrong digest here even though it is the stronger
# hash: that one belongs in SHA256SUMS.txt for people, not in the manifest for the server.
#
# The merge is deliberately narrow. Republishing a release is normal, so a rerun replaces the
# one entry it is about - same `version` and same `targetAbi` - and leaves every other published
# version alone; a manifest that forgets older revisions takes them away from servers that can
# still install them. `versions` is ordered newest-first because the 12 dashboard offers
# `versions[0]` as the default install rather than the newest entry.
#
# Nothing in this script touches git or the network. It reads the previously published document
# from a file, writes the next one, and refuses to emit a document it would not install.
#
# Exit status: 0 when the manifest was written and validated, 1 otherwise.

set -u

meta=''
version=''
source_url=''
checksum=''
changelog_file=''
previous='/dev/null'
timestamp=''
out=''

# A literal newline, for the single-line guards below. `grep` matches a pattern per line, so the
# anchored patterns further down are satisfied by a value whose *first* line happens to fit them
# and which carries whatever it likes on the lines below. Anything that is not one line is refused
# before a pattern is consulted at all, which is what makes `^` and `$` mean the ends of the field
# rather than the ends of its first line.
newline='
'

fail() {
    printf 'release-manifest: %s\n' "$*" >&2
    exit 1
}

usage() {
    printf '%s\n' 'usage: sh .ci/release-manifest.sh --meta FILE --version X.Y.Z \'
    printf '%s\n' '        --source-url URL --checksum MD5 --changelog-file FILE \'
    printf '%s\n' '        --out FILE [--previous FILE] [--timestamp TIMESTAMP]'
    printf '%s\n' '       --previous   the manifest already published; /dev/null for a first release'
    printf '%s\n' '       --timestamp  ISO-8601 UTC; defaults to the current time'
}

# Refuses a field that is not one line, naming the option that carried it. The changelog is the
# one value in this interface that is free text and may span lines; everything the server reads as
# a single token - the version, the ABI, the digest, the timestamp, the download URL - is not, and
# a stray newline inside one of them would otherwise survive into the published document as a
# string that merely *starts* with something valid.
single_line() {
    case "$1" in
        *"$newline"*)
            fail "$2 is more than one line; it is one value, not free text"
            ;;
    esac
}

# Every value the server will act on arrives as a named option, so a caller cannot half-supply
# one and get a manifest that silently means something else.
while [ "$#" -gt 0 ]; do
    case "$1" in
        --meta)
            [ "$#" -ge 2 ] || fail "--meta needs a file name"
            meta=$2
            shift 2
            ;;
        --version)
            [ "$#" -ge 2 ] || fail "--version needs a version"
            version=$2
            shift 2
            ;;
        --source-url)
            [ "$#" -ge 2 ] || fail "--source-url needs a URL"
            source_url=$2
            shift 2
            ;;
        --checksum)
            [ "$#" -ge 2 ] || fail "--checksum needs a digest"
            checksum=$2
            shift 2
            ;;
        --changelog-file)
            [ "$#" -ge 2 ] || fail "--changelog-file needs a file name"
            changelog_file=$2
            shift 2
            ;;
        --previous)
            [ "$#" -ge 2 ] || fail "--previous needs a file name"
            previous=$2
            shift 2
            ;;
        --timestamp)
            [ "$#" -ge 2 ] || fail "--timestamp needs a timestamp"
            timestamp=$2
            shift 2
            ;;
        --out)
            [ "$#" -ge 2 ] || fail "--out needs a file name"
            out=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage >&2
            fail "unrecognised argument '$1'"
            ;;
    esac
done

[ -n "$meta" ] || { usage >&2; fail '--meta is required'; }
[ -n "$version" ] || { usage >&2; fail '--version is required'; }
[ -n "$source_url" ] || { usage >&2; fail '--source-url is required'; }
[ -n "$checksum" ] || { usage >&2; fail '--checksum is required'; }
[ -n "$changelog_file" ] || { usage >&2; fail '--changelog-file is required'; }
[ -n "$out" ] || { usage >&2; fail '--out is required'; }

[ -r "$meta" ] || fail "there is no install record to read at '$meta'; run the packaging step first"
[ -r "$changelog_file" ] || fail "there is no changelog text to read at '$changelog_file'"
[ -r "$previous" ] || fail "'$previous' is not readable; pass --previous /dev/null for a first release"

# Rejected here rather than at the server: a `version` the server cannot parse as a
# System.Version is the one malformed field that does not merely skip its own entry. The 12
# catalogue load lets that FormatException escape, so a bad version in one entry takes the whole
# repository - and every other package in the document - down with it.
single_line "$version" '--version'
printf '%s' "$version" | grep -Eq '^[0-9]+(\.[0-9]+){1,3}$' \
    || fail "--version '$version' is not one to four dot-separated numbers; the server parses it as a System.Version"

# The install URL has to end in `.zip` because that is the test the server applies to the URL
# string before it downloads anything, and it has to be a release-asset URL because that address
# outlives the workflow run that wrote the bytes. A workflow-artifact URL would rot.
single_line "$source_url" '--source-url'
printf '%s' "$source_url" | grep -Eq '^https://[^[:space:]]+\.zip$' \
    || fail "--source-url '$source_url' is not an https URL whose path ends in .zip"

single_line "$checksum" '--checksum'
printf '%s' "$checksum" | grep -Eq '^[0-9a-f]{32}$' \
    || fail "--checksum '$checksum' is not a lowercase 32-character MD5 hex digest; Jellyfin 12 verifies this field with MD5"

if [ -z "$timestamp" ]; then
    timestamp=$(date -u '+%Y-%m-%dT%H:%M:%SZ') || fail "the clock could not be read; pass --timestamp"
fi

single_line "$timestamp" '--timestamp'
printf '%s' "$timestamp" | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$' \
    || fail "--timestamp '$timestamp' is not an ISO-8601 UTC time ending in Z"

# The four fields above arrive as options and are now known to be one line each, so the anchors in
# their patterns above and in the document check below both bind to the whole field. `targetAbi`
# and every version carried over from the previously published document do not arrive as options -
# they are read out of JSON - so the check on the finished document anchors those with `\A` and
# `\z` instead, which are the ends of the string and not the ends of its first line.

# The identity the server lists, and the ABI the entry is gated on, are copied out of the
# install record the packaging job wrote next to the zip. That is the same record whose `guid`
# the server compares against the one inside the archive, and a disagreement there marks an
# installed plugin malfunctioned - so the manifest is generated from that file rather than typed
# beside it.
merge='
def padded_version:
  ((.version | split(".")) | map(tonumber)) as $parts
  | $parts + [ range(4 - ($parts | length)) | 0 ];

def newest_first:
  sort_by([ (padded_version | map(0 - .)),
            (0 - (.timestamp | strptime("%Y-%m-%dT%H:%M:%SZ") | mktime)) ]);

($previous[0] // []) as $published
| ($meta[0]) as $m
| (if ($changelog | test("\\S"))
   then ($changelog | gsub("^\\s+|\\s+$"; ""))
   else ("Anaglyfin " + $version)
   end) as $notes
| { name: $m.name,
    description: $m.description,
    overview: $m.overview,
    owner: $m.owner,
    category: $m.category,
    guid: $m.guid,
    versions: [ { version: $version,
                  changelog: $notes,
                  targetAbi: $m.targetAbi,
                  sourceUrl: $sourceUrl,
                  checksum: $checksum,
                  timestamp: $timestamp } ] } as $fresh
| (if ($published | type) == "array"
   then [ $published[] | select(type == "object") ]
   else []
   end) as $packages
| ([ $packages[] | select((.guid // "") == $m.guid) ] | length) as $same
| ([ $packages[] | select((.guid // "") == $m.guid) ]
   | if (length > 0) then (.[0].versions // []) else [] end) as $already
| ([ $already[]
    | select(type == "object")
    | select((.version != $version) or (.targetAbi != $m.targetAbi)) ]) as $carried
# A rerun replaces the revision it is republishing and nothing else. Both halves of that key
# matter: one plugin version built for two server ABIs is two installable revisions.
| ($carried + $fresh.versions) as $together
# Newest-first, as numbers rather than as text, so 0.1.10 outranks 0.1.9. The short form is
# padded to four parts so 0.1.0 and 0.1.0.0 rank alike, and the publish time breaks a tie so the
# later build of one version is the one offered first.
| { versions: ($together | newest_first) } as $order
| ($fresh + $order) as $merged
| (if ($same > 0)
   then [ $packages[] | if ((.guid // "") == $m.guid) then $merged else . end ]
   else ($packages + [ $merged ])
   end)
'

# The same document, judged the way the server will judge it. Every refusal names its field,
# because whoever reads a red release run cannot inspect the manifest a server choked on.
check='
def filled($x): (($x | type) == "string") and ($x | test("\\S"));
# Every shape below is anchored with \A and \z rather than ^ and $. This regex engine treats ^
# and $ as line boundaries, so "^...$" is satisfied by a string whose first line fits it no matter
# what follows on the rest - and targetAbi and the carried-over versions come out of JSON, where
# nothing upstream has promised they are one line. \A and \z are the ends of the string.
def shaped($x; $re): (($x | type) == "string") and ($x | test($re));
. as $doc
| (if ($doc | type) == "array" then $doc else [] end) as $packages
| [ $packages[]
    | . as $p
    | (if ($p | type) == "object"
       then (($p.name // $p.guid // "?") | tostring)
       else "?"
       end) as $who
    | if ($p | type) != "object"
      then ["a package entry is a \($p | type), not an object"]
      else [ ( "name", "description", "overview", "owner", "category" ) as $field
             | select(filled($p[$field]) | not)
             | "\($who): \($field) must be a non-empty string" ]
           + [ ( if shaped($p.guid; "\\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\z")
                 then empty
                 else "\($who): guid \($p.guid) is not the GUID the server matches the installed plugin on" end ),
               ( if ($p.versions | type) == "array"
                 then empty
                 else "\($who): versions must be an array" end ),
               ( if (($p.versions | type) == "array") and (($p.versions | length) > 0)
                 then empty
                 else "\($who): versions must hold at least one published release" end ) ]
           + ( if ($p.versions | type) == "array"
               then [ $p.versions[]
                      | . as $v
                      | (if ($v | type) == "object"
                         then (($v.version // "?") | tostring)
                         else "\($v | type)"
                         end) as $at
                      | if ($v | type) != "object"
                        then ["\($who) \($at): a version entry is a \($v | type), not an object"]
                        else [ ( if shaped($v.version; "\\A[0-9]+(\\.[0-9]+){1,3}\\z")
                                 then empty
                                 else "\($who) \($at): version must be one to four dot-separated numbers; the server parses it as a System.Version, and one it cannot parse fails the whole catalogue load" end ),
                               ( if shaped($v.targetAbi; "\\A[0-9]+(\\.[0-9]+){1,3}\\z")
                                 then empty
                                 else "\($who) \($at): targetAbi must be a version number such as 12.0.0" end ),
                               ( if shaped($v.sourceUrl; "\\Ahttps://[^[:space:]]+\\.zip\\z")
                                 then empty
                                 else "\($who) \($at): sourceUrl must be an anonymously downloadable https URL whose path ends in .zip" end ),
                               ( if shaped($v.checksum; "\\A[0-9a-f]{32}\\z")
                                 then empty
                                 else "\($who) \($at): checksum must be the zip digest as lowercase 32-character hex; Jellyfin 12 verifies it with MD5" end ),
                               ( if shaped($v.timestamp; "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\\z")
                                 then empty
                                 else "\($who) \($at): timestamp must be an ISO-8601 UTC time ending in Z; the server writes it into the installed plugin manifest" end ),
                               ( if filled($v.changelog)
                                 then empty
                                 else "\($who) \($at): changelog must carry the release text the dashboard shows" end ) ]
                        end
                    ]
               else []
               end )
           + ( if ($p.versions | type) == "array"
               then [ ($p.versions | map(select(type == "object"))) as $builds
                      | [ $builds[] | "\(.version)|\(.targetAbi)" ] as $keys
                      | select(($keys | length) != ($keys | unique | length))
                      | "\($who): two version entries share one version and targetAbi, so the server would see two revisions of the same build" ]
               else []
               end )
      end
   ] as $found
| ( ($packages | length) as $n
    | [ ( if ($doc | type) == "array"
          then empty
          else "the manifest is a \($doc | type); Jellyfin reads a top-level array of package objects" end ),
        ( if ($n == 1)
          then empty
          else "the manifest holds \($n) package entries; this repository publishes exactly one plugin" end ) ] ) as $shape
| ($shape + ($found | flatten)) as $problems
| if ($problems | length) == 0
  then .
  else ("the repository manifest would not install:\n"
        + ([ $problems[] | "  - " + . ] | join("\n"))
        | error)
  end
'

mkdir -p "$(dirname "$out")" || fail "the directory for '$out' cannot be created"

# Probed here rather than with the arguments above: a caller who passed a malformed version wants
# to hear about the version first, whether or not this machine happens to have jq on its PATH.
command -v jq > /dev/null 2>&1 || fail "jq is required to build and validate the manifest"

# `--previous` and `--changelog-file` are read as files rather than passed as arguments. Release
# text is multi-line and free-form, and an empty previous document has to arrive as "no
# published versions" rather than as an empty string that parses as nothing at all.
if ! jq -n \
        --slurpfile previous "$previous" \
        --slurpfile meta "$meta" \
        --rawfile changelog "$changelog_file" \
        --arg version "$version" \
        --arg sourceUrl "$source_url" \
        --arg checksum "$checksum" \
        --arg timestamp "$timestamp" \
        "$merge" > "$out.part"
then
    rm -f "$out.part"
    fail "the manifest could not be built from '$meta' and '$previous'"
fi

if ! jq -e "$check" "$out.part" > /dev/null
then
    rm -f "$out.part"
    fail "'$out.part' was refused; the validated manifest was not written"
fi

# Moved rather than written in place, so '$out' is only ever a document that passed the check.
mv "$out.part" "$out" || fail "'$out.part' could not be moved into place as '$out'"

# The count is the merge's own evidence: a republish reports the revisions it kept beside the one
# it replaced, so a lost version shows up in the release log instead of only in a catalogue a
# month later.
versions=$(jq -r '[ .[] | (.versions | length) ] | add // 0' "$out")
if [ "$versions" -eq 1 ]; then
    entries='entry'
else
    entries='entries'
fi

printf 'release-manifest: %s (%s) published as version %s; the document holds %s version %s\n' \
    "$(jq -r '.[0].name' "$out")" \
    "$(jq -r '.[0].guid' "$out")" \
    "$version" \
    "$versions" \
    "$entries"
