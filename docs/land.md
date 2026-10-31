# Land and region (SojournLand)

Written by Claude Opus 5.5, 2026-10-28 (batch B5 of `docs/viewer-gaps.md` > Closing the rest). Agent-facing. Each
feature as the official viewer does it (its source read for each, named below), an op on `SojournLand` and, for
everything that spends, gives land away, ejects or bans, deletes or changes who may do what, a confirmation the
person presses. No menu rows or settings: the person asks the assistant, whose `land` tool (`assistant/moretools.py`)
covers about, region, covenant, access and the lists, eject, freeze, passes, buying, land for sale, About Land's
settings, lag and returns, the region's switches, terraforming and holdings; selling, dividing, joining, deeding and
the estate's lists go through `client_api`. None of it is on incognito's blind allowlist
(`Session/Private/Allowlist.cs`); with "let it see" it runs as usual, gated as always.

Code: `Core/Agents/ParcelOps.cs` (About Land, the lists, the parcel by place), `ParcelOps.Deals.cs` (sell, buy, pass,
abandon, deed, reclaim, divide, join), `ParcelOps.Terraform.cs`, `EstateOps.cs` (region, covenant, estate),
`LandMap.cs` (holdings, contributions, map items), `SearchOps.cs` (`LandAsync`), `AgentSession.Land.cs`;
`Leap/Apis/SojournLand.cs`, `SojournSearch.cs` (`land`); the client half `Client/Land/LandWiring.cs` (confirmations,
the land shown, the brush, the collision hook); the drawing `Render/World/ParcelLines.Selection.cs`. Hunks in shared
files: `ParcelLines.cs` (partial, one call), `Terrain.cs` (`HurryUntil`), `StockApis.cs`, `TesselAgent.cs`
(`parcelSet` stays the parcel you stand on, as its confirmation says), `Program.cs` (one line and its dispose).
`TesselAgent`'s parcel ops (`parcelInfo`, `parcelSet`, `parcelObjects`, `parcelReturn`, `parcelEject`, `parcel`) are
unchanged and call the same `ParcelOps`.

## Which parcel

Every parcel op works on the parcel you stand on, or the one at `at: [x, y]` (this region's metres), or one already
pinned by `local_id` (+ `region`). The parcel is asked for afresh each time (ParcelPropertiesRequest by point or by
id), never read from a cache, since the region answers most land changes with no word but the parcel's new
properties: every change is read back and a refusal said ("the region didn't change ...: only the parcel's owner, or
a group member with the powers, may"). Only parcels in the region you're in: the land messages go to its host, as the
viewer's selection does.

## About Land

- `parcel {at?}`: the floater's General, Options and Access at a glance: name, description, owner (a name), group,
  area, bounds, category, the options, sale (`for_sale {price, buyer, objects_included}`), pass (`price, hours`), prims
  (used, max, owner's, group's, others'), auto-return minutes, `see_avatars`, landing, music and media. It also
  outlines the parcel on the ground for 10 s (below).
- `set`: `parcelSet`'s fields plus `category`, `pass_price`, `pass_hours`, `auto_return_minutes`, `see_avatars`
  (llparcel's SeeAVs: others outside see and hear avatars here) and the options `access_list`, `access_group`,
  `pass_list`, `deny_no_payment_info`, `deny_under_18`, `voice`, `voice_parcel_only`; Parcel.Update
  (ParcelPropertiesUpdate cap, else the UDP message, then ParcelSetOtherCleanTime), read back.
- `access`: the allowed and banned lists (ParcelAccessListRequest, AL_ACCESS and AL_BAN; an empty list comes as one
  blank entry), each `{id, name, until?}`, and who it's open to. `allow`, `unallow`, `ban`, `unban {to | ids, hours?}`:
  as the floater, the whole list goes back (LLViewerParcelMgr::sendParcelAccessListUpdate: ParcelAccessListUpdate, 48
  entries a message, numbered, a transaction id; an empty list is one blank block), each entry's time a Unix expiry
  (0: for good), at most 300 (PARCEL_MAX_ACCESS_LIST); the owner can't be listed. Then the list is read again. Adding
  to the allowed list says so when the parcel isn't closed to it (`options.access_list`).
- `freeze {to, freeze}`: About Land's Freeze (FreezeUser); freeing isn't asked.
- `to`: an id, or the name of someone in the region or a friend; anyone else by id (TesselAgent `findAvatar`).

## Land changing hands

From `llviewerparcelmgr.cpp`, `llfloaterbuyland.cpp`, `llfloatersellland.cpp`:
- `sell {price, to? | to_group?, objects?}`: ForSale with the price and the authorised buyer (none: anyone), and
  SellParcelObjects for "sell the objects with the land". A sale to anyone needs a price above L$0
  (SalePriceRestriction). `stopSelling` isn't asked.
- `buy {at?, group?}`: LLFloaterBuyLandUI's checks first (for sale, priced or for one buyer, that buyer you or a group
  of yours with deed and set-sale powers, not already yours, your balance), then ParcelBuy with the price and area the
  person was shown (pinned by the confirmation; the region checks them again, so a seller's change since fails it)
  and Final (no escrow), the active group as the viewer sends it. Done when the parcel's owner is you (15 s). Not
  done: the viewer's preflight with Linden Lab's website for mainland tier and buying L$ (`preflightBuyLandPrep`); what
  the region does with a buy the account's land use doesn't cover is unchecked, and the confirmation says Premium and
  the land use fee apply on the mainland. Claiming public land (ParcelClaim) isn't offered.
- `buyPass {at?}`: ParcelBuyPass. Without `at`, the parcel you stand on if it sells passes, else the one that turned you
  away in the last ten minutes (ParcelProperties with a collision sequence id, which `LandWiring` passes to
  `ParcelOps.Observe`), as the viewer offers the pass from the ban line. The grid's MoneyBalanceReply says whether it
  was sold.
- `abandon` (ParcelRelease; yours, or a group's you have the release power in), `deed {group?}` (ParcelDeedToGroup; to
  the parcel's group, set first when another of yours is named), `reclaim` (ParcelReclaim; the region's owner takes a
  parcel back). Each watched until the owner changes.
- `contribute {group, area}`: the group's Land & L$ "your contribution" (SetGroupContribution).

## Shape: dividing, joining, terraforming

- `divide {half | west, south, east, north}`: LLViewerParcelMgr::callbackDivideLand, ParcelDivide on a rectangle of 4 m
  cells inside one parcel (snapped outward); `half: north|south|east|west` halves the parcel's bounds. The new part
  keeps the parcel's settings.
- `join {at: [[x, y], [x, y]] | rectangle}`: ParcelJoin; every parcel the rectangle touches becomes one (the region
  wants them all one owner's; the largest keeps its settings). Checked by the rectangle's two corners landing in one
  parcel after.
- `terraform {action, at?, size?, seconds?, height?, parcel? | rectangle}`: lltoolbrush.cpp. ModifyLand with the
  viewer's actions (flatten 0, raise, lower, smooth, roughen, revert 5), the brush's old index (getBrushIndex: the last
  of 1, 2, 4 m the size is bigger than) and its float size in ModifyBlockExtended, Height the ground where the stroke
  starts (flatten's target, or `height`). The viewer sends a message a frame while the button is held, each worth the
  frame's time times LandBrushForce (1); here `seconds` (1 by default, up to 10) goes as pulses ten a second. Over a
  parcel or rectangle, the selection's weights (flatten, raise, lower x0.25, smooth x5, roughen x0.5, revert 0.5 once).
  Refused before sending where the region blocks terraforming or the parcel is someone else's without its terraform
  option (estate managers may anywhere). The answer gives the ground's height at the centre before and after; a raise
  or lower that didn't move it says so.

## What's drawn

`World/ParcelLines.Selection.cs`, with the property lines (depth-tested, writing depth), in both eyes:
- **The land concerned**: the viewer's selection highlight (renderHighlightSegments, llglsandbox.cpp): 0.666 m posts of
  20% yellow along the outline of the parcel's cells (its bitmap) or a rectangle, each edge from the ground at its two
  ends. The viewer shows the land you selected by hand; here the assistant picked it, so it's shown while the person is
  asked (up to the confirmation's two minutes) and 8 s after the answer, and 10 s after `parcel` or `access`.
- **The brush**: LLToolBrushLand::renderOverlay's white sticks, one at each metre of the brush's square, taller toward
  the middle, while a stroke goes and 3 s after (crossed 3 cm quads; the viewer's are one-pixel lines). Over an area,
  its outline in white instead.
- **The land's change**: the region sends the changed patches back and `Terrain` rebuilds; while a stroke goes
  (`Terrain.HurryUntil`) it rebuilds within a quarter second instead of waiting 0.5 s for the patches to settle or 1.5 s
  while they stream.

## Holdings, the map, search

- `holdings {group?}`: My Land Holdings (llfloaterlandholdings.cpp): PlacesQuery with DFQ_AGENT_OWNED (the reply's
  QueryID is the transaction id over UDP and the query id over the event queue, so both are matched), each
  `{name, region, area, billable_area, go_to}`, the totals, and each group's contribution from the group data; with
  `group`, that group's land (DFQ_GROUP_OWNED, as its Land & L$ tab).
- `mapItems {type, region?, limit}`: MapItemRequest, as llworldmap.cpp asks: land for sale (general and adult), events
  by rating, telehubs grid-wide; people a region at a time (this one by default). Nearest first, each in its region
  (named from the grid's region list, asked by handle where not known) with a `go_to`. The map board reads
  `LandMap.ItemsAsync` (docs/map.md).
- `SojournSearch land {type?: mainland|estate|auction, max_price?, min_area?, sort?}`: the legacy search's Land Sales
  tab (DirLandQuery, cheapest per m² first by default; the reply has no query id, so one at a time), each filled in by
  ParcelInfoRequest with its region and `go_to`, as places are.

## Region and estate

From `llfloaterregioninfo.cpp`, `llfloatertopobjects.cpp`:
- For anyone: `region` (RequestRegionInfo, which any agent may ask: name, product type, maturity, agent limit and its
  most, object bonus, water height, terrain limits, the General tab's switches, the Debug tab's, `i_manage`, and the
  restart schedule where the grid has the RegionSchedule cap) and `covenant` (EstateCovenantRequest, then its notecard
  as an estate asset; "none" when there's no covenant).
- For the estate's owner and managers (`EstateOps.CanManage`: RegionHandshake's estate-manager flag or the region's
  owner; refused before sending otherwise, as the viewer greys its controls): `estate` (the EstateAccess capability on
  Second Life and OpenSim, with ban dates and who banned on SL; else getinfo's setaccess per list; the estate's name,
  owner and switches from getinfo's estateupdateinfo), `estateAdd`,
  `estateRemove {list: managers|allowed|allowed_groups|banned, all_estates?}` (estateaccessdelta, then read back),
  `kick` (kickestate), `sendHome {to | everyone}` (teleporthomeuser, teleporthomeallusers), `message {text, estate?}`
  (simulatormessage to the region, instantmessage to the estate), `setRegion` (setregioninfo or DispatchRegionInfo,
  every field kept unless named), `regionDebug` (setregiondebug, the three kept unless named), `restart {delay 30-240,
  120 | cancel}`, `topScripts`, `topColliders {owner | object | parcel}` (LandStatRequest with STAT_FILTER_BY_OWNER,
  OBJECT or PARCEL_NAME), `returnObjects {ids}` (ParcelReturnObjects, the whole region, by task id, as Top Objects'
  Return), `bakeTerrain` (terrain bake).

## What waits for the person

`LandWiring` adds a `Confirmations.Cases` entry (no hunk in `Confirmations.cs`). Each description is the region's own
answer about the land, asked when the confirmation comes up, and the op's arguments are pinned to it (`local_id`,
`region`; `price` and `area` for a buy, `price` for a pass), so the op acts on what the person was shown. The land is
outlined on the ground meanwhile.
- **spend**: `buy` (price, seller, your balance, the covenant's first lines, and the mainland's Premium and land use
  fee; a parcel that can't be bought says why), `buyPass` (price, hours), `contribute`.
- **give**: `sell` (to whom; L$0 says the land is given away), `abandon`, `deed`.
- **permissions**: `set` (the changes listed), `allow`, `unallow`, `ban`, `unban`, `freeze`, `reclaim`, `divide`,
  `estateAdd`, `estateRemove` (a manager's powers said), `kick`, `sendHome`, `setRegion`, `regionDebug`, `restart`
  (everyone warned, then logged out).
- **delete**: `join` (the others' names, settings and lists are lost), `terraform` over a parcel or area (revert only
  goes back to the last bake), `returnObjects` (the objects' names from the last top list), `bakeTerrain`.
- **words**: `message` (the exact text).
- Not asked: reading anything, `stopSelling`, unfreezing, a terraform brush stroke at a point (small, and the person
  watches the land move), `restart {cancel}`.

## Testing

`python tools/land.py [--dll DIR] [--only region,estate,top,message,debug,restart,access,freeze,deals,pass,terraform,search,home]`:
two clients in Atelier Northeast, Estate Keeper (the Atelier estate's owner and every parcel's; the script gives it the
grid's usual password the first time) and Sojourn Friend, both with SOJOURN_TEST_INPUT=1 so posted clicks answer
their confirmations (found as `tools/people.py` finds them). What it changes it changes back.

It covers the region and covenant reads, the estate's lists (refused to a visitor; added and removed), top lists, a
region message, scripts off and on, a restart cancelled at its confirmation, a ban for an hour and its removal, the
allowed list, freeze, the parcel halved, sold, bought, renamed, reclaimed, abandoned and joined again, a pass sold and
bought, terraforming at a point (and a visitor's refused, a whole parcel's asked), the map's people and land for sale,
and sending home; all passed 2026-10-29. OpenSim can't check holdings (it answers no PlacesQuery for owned land), land
search, top lists with anything in them, a covenant's text, or deeding (no group on these accounts).

Gotchas the local grid showed (handled in the code): LibreMetaverse raises ParcelAccessListReply only with
`Settings.Parcel.AlwaysRequestAcl` (which asks every parcel's lists unbidden), so the packet is read directly;
OpenSim leaves ParcelPropertiesRequestByID unanswered, so a pinned parcel is asked for by a point inside it (from its
last bitmap); OpenSim has the EstateAccess capability but names a manager's id `agent_id`, and applies estate list
changes after a delay, so they're read back for up to 11 s; LibreMetaverse's estateupdateinfo reads the estate id and
sun hour as raw bytes of what are strings (the id is the region's, from RegionInfo, instead); a float put in a
JsonArray reads back as nothing through `Json.AsDouble`, so points are doubles.

## Not done

- The estate's own settings (EstateChangeInfo: public access, the deny switches, the sun): LibreMetaverse reads the
  estate's sun hour as bytes rather than the string it is, so sending its flags back would set a wrong sun; ours to
  parse when it's wanted. Telehubs, region terrain textures and heights, terrain upload and download.
- The buy floater's website preflight (mainland tier upgrades, buying L$ for the price): Linden Lab's web, with the
  person's payment details; the user's call with the Marketplace and L$ (viewer-gaps > Waiting on the user's decision).
- Claiming public land (ParcelClaim), auctions.
- A pass offered on its own when you hit a pass-restricted line (the viewer's BuyPass notice): the ban lines are drawn;
  the offer would go on the feed as a notice with a key.

## Checks only Second Life can do

For the person; nothing here spends money unless they press for it:
1. Stand on any parcel and ask "whose land is this, what are its rules?": the owner's name, area, options and, if any,
   the covenant read out. The yellow posts outline the parcel for a few seconds.
2. "What does this estate's covenant say?" in an estate region (not mainland): the text arrives.
3. "What region is this, how many people can it hold?": `region` answers, `i_manage` false.
4. "Find me land for sale under L$2000" (`SojournSearch land`): results with regions and go_to; "show me land for sale
   on the map near here" (`mapItems land_for_sale`): nearby parcels with prices.
5. "Show me my land holdings": your parcels if any, group contributions.
6. A pass-restricted club (its ban line says "pass"): after being turned away, "buy a pass" shows the price and hours;
   press only if you want to go in (it spends L$).
7. If you own or rent land with the powers: "who's banned from my land?", then ban and unban an alt or a friend who
   agrees, for an hour. The confirmation's description and the posts on the ground should match the parcel.
8. If you manage an estate: `estate` lists (with ban dates), and `topScripts` during a busy hour.
