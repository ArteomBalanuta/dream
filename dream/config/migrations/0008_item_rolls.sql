-- Migration 0008: rolled item stats (wave H). NULL means an unrolled item, whose stats come from its
-- definition. The value is versioned JSON (ItemRollCodec) and never changes after the item is
-- created. No table is rebuilt, so trg_item_instances_advance_object_id stays.
ALTER TABLE item_instances ADD COLUMN rolled_modifiers TEXT NULL
    CHECK (rolled_modifiers IS NULL OR (length(rolled_modifiers) <= 1024 AND json_valid(rolled_modifiers)));
