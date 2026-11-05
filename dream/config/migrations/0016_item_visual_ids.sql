-- Realign stored item visual ids with the shipped content catalog.
--
-- item_instances.visual_id is a denormalised copy of the catalog's value: GrantItem writes
-- ToolDefinition.VisualId at creation, and PlayerInventorySnapshotJsonConverter already falls back to
-- definition.VisualId when the wire omits the field. The column is therefore expected to mirror the
-- catalog for every known kind, and nothing ever writes a different value on purpose.
--
-- The worn-equipment-visuals wave broke that for existing saves. Before it, all twenty armour items
-- shared nine flat per-slot ids ('worn.head', 'worn.body', 'worn.back', ...) because nothing rendered
-- them; the wave gave each item its own id ('worn.head.leather-cap') and six held items were renamed
-- the same way. items.json changed, but the rows already written did not, so a character who owned
-- gear before the merge still carries the old id. ArmourMeshCatalog.Resolve returns null for an
-- unknown worn id and deliberately draws NOTHING rather than a wrong box, so that character wears a
-- full set of armour and sees none of it. Reported from a live game: head, body, hands, feet, legs,
-- back and companion all equipped, all stored as the old flat ids, all invisible.
--
-- This sets visual_id from the catalog for every known kind. It is written as an unconditional
-- assignment rather than a targeted rename so it is idempotent and cannot leave a half-migrated row:
-- a row already holding the current value is rewritten to the same value. Rows whose item_kind is not
-- in the catalog are left exactly as stored - an unknown kind is already inert in TryResolveItem, and
-- guessing a visual for it would be worse than leaving it alone.
--
-- A future rename of a visual id needs its own migration, for the same reason this one exists.

UPDATE item_instances
SET visual_id = CASE item_kind
        WHEN 'consumable.greater-heal' THEN 'consumable.potion.heal'
        WHEN 'consumable.greater-mana' THEN 'consumable.potion.mana'
        WHEN 'consumable.minor-heal' THEN 'consumable.potion.heal'
        WHEN 'consumable.minor-mana' THEN 'consumable.potion.mana'
        WHEN 'consumable.weak-heal' THEN 'consumable.potion.heal'
        WHEN 'gear.ash-staff' THEN 'held.weapon.staff'
        WHEN 'gear.battle-axe' THEN 'held.weapon.axe'
        WHEN 'gear.blood-ring' THEN 'worn.ring.blood-ring'
        WHEN 'gear.bone-amulet' THEN 'worn.neck.bone-amulet'
        WHEN 'gear.bone-ring' THEN 'worn.ring.bone-ring'
        WHEN 'gear.bone-staff' THEN 'held.weapon.bone-staff'
        WHEN 'gear.chain-chausses' THEN 'worn.legs.chain-chausses'
        WHEN 'gear.chain-hauberk' THEN 'worn.body.chain-hauberk'
        WHEN 'gear.ember-wisp' THEN 'worn.companion.ember-wisp'
        WHEN 'gear.flanged-mace' THEN 'held.weapon.mace'
        WHEN 'gear.gossamer-wings' THEN 'worn.back.gossamer-wings'
        WHEN 'gear.grave-mantle' THEN 'worn.back.grave-mantle'
        WHEN 'gear.greathelm' THEN 'worn.head.greathelm'
        WHEN 'gear.greatsword' THEN 'held.weapon.greatsword'
        WHEN 'gear.hand-axe' THEN 'held.weapon.hand-axe'
        WHEN 'gear.hollow-locket' THEN 'worn.neck.hollow-locket'
        WHEN 'gear.hooded-cloak' THEN 'worn.back.hooded-cloak'
        WHEN 'gear.hunting-spear' THEN 'held.weapon.spear'
        WHEN 'gear.iron-band' THEN 'worn.ring.iron-band'
        WHEN 'gear.iron-blade' THEN 'held.weapon.iron-blade'
        WHEN 'gear.iron-boots' THEN 'worn.feet.iron-boots'
        WHEN 'gear.iron-dagger' THEN 'held.weapon.dagger'
        WHEN 'gear.iron-gauntlets' THEN 'worn.hands.iron-gauntlets'
        WHEN 'gear.iron-helm' THEN 'worn.head.iron-helm'
        WHEN 'gear.iron-torc' THEN 'worn.neck.iron-torc'
        WHEN 'gear.kite-shield' THEN 'held.shield.kite'
        WHEN 'gear.leather-boots' THEN 'worn.feet.leather-boots'
        WHEN 'gear.leather-cap' THEN 'worn.head.leather-cap'
        WHEN 'gear.leather-gloves' THEN 'worn.hands.leather-gloves'
        WHEN 'gear.leather-leggings' THEN 'worn.legs.leather-leggings'
        WHEN 'gear.leather-vest' THEN 'worn.body.leather-vest'
        WHEN 'gear.long-bow' THEN 'held.weapon.bow'
        WHEN 'gear.long-sword' THEN 'held.weapon.sword'
        WHEN 'gear.plate-cuirass' THEN 'worn.body.plate-cuirass'
        WHEN 'gear.plate-gauntlets' THEN 'worn.hands.plate-gauntlets'
        WHEN 'gear.plate-greaves' THEN 'worn.legs.plate-greaves'
        WHEN 'gear.plate-sabatons' THEN 'worn.feet.plate-sabatons'
        WHEN 'gear.plated-leggings' THEN 'worn.legs.plated-leggings'
        WHEN 'gear.short-bow' THEN 'held.weapon.short-bow'
        WHEN 'gear.short-sword' THEN 'held.weapon.short-sword'
        WHEN 'gear.silver-ring' THEN 'worn.ring.silver-ring'
        WHEN 'gear.tower-shield' THEN 'held.shield.tower'
        WHEN 'gear.travel-cloak' THEN 'worn.back.travel-cloak'
        WHEN 'gear.war-hammer' THEN 'held.weapon.hammer'
        WHEN 'gear.ward-lantern' THEN 'held.gear.ward-lantern'
        WHEN 'gear.ward-pendant' THEN 'worn.neck.ward-pendant'
        WHEN 'gear.wooden-buckler' THEN 'held.shield.buckler'
        WHEN 'potion.health' THEN 'consumable.potion.heal'
        WHEN 'potion.mana' THEN 'consumable.potion.mana'
        WHEN 'tool.builder-hammer' THEN 'held.tool.builder'
        WHEN 'tool.field-blade' THEN 'held.tool.blade'
        WHEN 'tool.stone-maul' THEN 'held.tool.maul'
        WHEN 'tool.survey-lantern' THEN 'held.tool.lantern'
        WHEN 'tool.torch' THEN 'held.tool.torch'
    END
WHERE item_kind IN ('consumable.greater-heal', 'consumable.greater-mana', 'consumable.minor-heal', 'consumable.minor-mana', 'consumable.weak-heal', 'gear.ash-staff', 'gear.battle-axe', 'gear.blood-ring', 'gear.bone-amulet', 'gear.bone-ring', 'gear.bone-staff', 'gear.chain-chausses', 'gear.chain-hauberk', 'gear.ember-wisp', 'gear.flanged-mace', 'gear.gossamer-wings', 'gear.grave-mantle', 'gear.greathelm', 'gear.greatsword', 'gear.hand-axe', 'gear.hollow-locket', 'gear.hooded-cloak', 'gear.hunting-spear', 'gear.iron-band', 'gear.iron-blade', 'gear.iron-boots', 'gear.iron-dagger', 'gear.iron-gauntlets', 'gear.iron-helm', 'gear.iron-torc', 'gear.kite-shield', 'gear.leather-boots', 'gear.leather-cap', 'gear.leather-gloves', 'gear.leather-leggings', 'gear.leather-vest', 'gear.long-bow', 'gear.long-sword', 'gear.plate-cuirass', 'gear.plate-gauntlets', 'gear.plate-greaves', 'gear.plate-sabatons', 'gear.plated-leggings', 'gear.short-bow', 'gear.short-sword', 'gear.silver-ring', 'gear.tower-shield', 'gear.travel-cloak', 'gear.war-hammer', 'gear.ward-lantern', 'gear.ward-pendant', 'gear.wooden-buckler', 'potion.health', 'potion.mana', 'tool.builder-hammer', 'tool.field-blade', 'tool.stone-maul', 'tool.survey-lantern', 'tool.torch')
  AND visual_id <> CASE item_kind
        WHEN 'consumable.greater-heal' THEN 'consumable.potion.heal'
        WHEN 'consumable.greater-mana' THEN 'consumable.potion.mana'
        WHEN 'consumable.minor-heal' THEN 'consumable.potion.heal'
        WHEN 'consumable.minor-mana' THEN 'consumable.potion.mana'
        WHEN 'consumable.weak-heal' THEN 'consumable.potion.heal'
        WHEN 'gear.ash-staff' THEN 'held.weapon.staff'
        WHEN 'gear.battle-axe' THEN 'held.weapon.axe'
        WHEN 'gear.blood-ring' THEN 'worn.ring.blood-ring'
        WHEN 'gear.bone-amulet' THEN 'worn.neck.bone-amulet'
        WHEN 'gear.bone-ring' THEN 'worn.ring.bone-ring'
        WHEN 'gear.bone-staff' THEN 'held.weapon.bone-staff'
        WHEN 'gear.chain-chausses' THEN 'worn.legs.chain-chausses'
        WHEN 'gear.chain-hauberk' THEN 'worn.body.chain-hauberk'
        WHEN 'gear.ember-wisp' THEN 'worn.companion.ember-wisp'
        WHEN 'gear.flanged-mace' THEN 'held.weapon.mace'
        WHEN 'gear.gossamer-wings' THEN 'worn.back.gossamer-wings'
        WHEN 'gear.grave-mantle' THEN 'worn.back.grave-mantle'
        WHEN 'gear.greathelm' THEN 'worn.head.greathelm'
        WHEN 'gear.greatsword' THEN 'held.weapon.greatsword'
        WHEN 'gear.hand-axe' THEN 'held.weapon.hand-axe'
        WHEN 'gear.hollow-locket' THEN 'worn.neck.hollow-locket'
        WHEN 'gear.hooded-cloak' THEN 'worn.back.hooded-cloak'
        WHEN 'gear.hunting-spear' THEN 'held.weapon.spear'
        WHEN 'gear.iron-band' THEN 'worn.ring.iron-band'
        WHEN 'gear.iron-blade' THEN 'held.weapon.iron-blade'
        WHEN 'gear.iron-boots' THEN 'worn.feet.iron-boots'
        WHEN 'gear.iron-dagger' THEN 'held.weapon.dagger'
        WHEN 'gear.iron-gauntlets' THEN 'worn.hands.iron-gauntlets'
        WHEN 'gear.iron-helm' THEN 'worn.head.iron-helm'
        WHEN 'gear.iron-torc' THEN 'worn.neck.iron-torc'
        WHEN 'gear.kite-shield' THEN 'held.shield.kite'
        WHEN 'gear.leather-boots' THEN 'worn.feet.leather-boots'
        WHEN 'gear.leather-cap' THEN 'worn.head.leather-cap'
        WHEN 'gear.leather-gloves' THEN 'worn.hands.leather-gloves'
        WHEN 'gear.leather-leggings' THEN 'worn.legs.leather-leggings'
        WHEN 'gear.leather-vest' THEN 'worn.body.leather-vest'
        WHEN 'gear.long-bow' THEN 'held.weapon.bow'
        WHEN 'gear.long-sword' THEN 'held.weapon.sword'
        WHEN 'gear.plate-cuirass' THEN 'worn.body.plate-cuirass'
        WHEN 'gear.plate-gauntlets' THEN 'worn.hands.plate-gauntlets'
        WHEN 'gear.plate-greaves' THEN 'worn.legs.plate-greaves'
        WHEN 'gear.plate-sabatons' THEN 'worn.feet.plate-sabatons'
        WHEN 'gear.plated-leggings' THEN 'worn.legs.plated-leggings'
        WHEN 'gear.short-bow' THEN 'held.weapon.short-bow'
        WHEN 'gear.short-sword' THEN 'held.weapon.short-sword'
        WHEN 'gear.silver-ring' THEN 'worn.ring.silver-ring'
        WHEN 'gear.tower-shield' THEN 'held.shield.tower'
        WHEN 'gear.travel-cloak' THEN 'worn.back.travel-cloak'
        WHEN 'gear.war-hammer' THEN 'held.weapon.hammer'
        WHEN 'gear.ward-lantern' THEN 'held.gear.ward-lantern'
        WHEN 'gear.ward-pendant' THEN 'worn.neck.ward-pendant'
        WHEN 'gear.wooden-buckler' THEN 'held.shield.buckler'
        WHEN 'potion.health' THEN 'consumable.potion.heal'
        WHEN 'potion.mana' THEN 'consumable.potion.mana'
        WHEN 'tool.builder-hammer' THEN 'held.tool.builder'
        WHEN 'tool.field-blade' THEN 'held.tool.blade'
        WHEN 'tool.stone-maul' THEN 'held.tool.maul'
        WHEN 'tool.survey-lantern' THEN 'held.tool.lantern'
        WHEN 'tool.torch' THEN 'held.tool.torch'
    END;
