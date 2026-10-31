-- Migration 0007: accounts and character ownership (wave G). No table is rebuilt. player_name stays
-- the immutable key; display_name becomes unique ignoring ASCII case.
CREATE TABLE accounts (
    account_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    token_hash BLOB NOT NULL,
    created_utc TEXT NOT NULL,
    last_seen_utc TEXT NOT NULL,
    CHECK (length(token_hash) = 32)
);

CREATE UNIQUE INDEX ux_accounts_token_hash ON accounts (token_hash);

ALTER TABLE player_profiles ADD COLUMN account_id INTEGER NULL
    REFERENCES accounts(account_id) ON DELETE SET NULL;

CREATE INDEX ix_player_profiles_account_id
    ON player_profiles (account_id) WHERE account_id IS NOT NULL;

-- Rows sharing a display name ignoring case: the oldest keeps it (created_utc, then player_name);
-- each other row gets "<its own display name>~<n>", with n the lowest free numbers from 2. A name
-- ending in ~digits cannot collide across groups, and the number range cannot run out: it holds
-- more values than there are renamed rows plus rows containing '~'.
CREATE INDEX ix_player_profiles_display_name__v7 ON player_profiles (display_name COLLATE NOCASE);

CREATE TABLE display_name_fix__v7 (
    player_name TEXT NOT NULL PRIMARY KEY,
    new_display_name TEXT NOT NULL
);

INSERT INTO display_name_fix__v7 (player_name, new_display_name)
WITH RECURSIVE
ranked AS (
    SELECT player_name, display_name,
        ROW_NUMBER() OVER (PARTITION BY display_name COLLATE NOCASE ORDER BY created_utc, player_name) AS duplicate_rank,
        MIN(display_name) OVER (PARTITION BY display_name COLLATE NOCASE) AS group_id
    FROM player_profiles
),
losers AS (
    SELECT player_name, display_name, group_id, duplicate_rank - 1 AS slot
    FROM ranked WHERE duplicate_rank > 1
),
numbers(n) AS (
    SELECT 2
    UNION ALL
    SELECT n + 1 FROM numbers
    WHERE n <= (SELECT COUNT(*) FROM losers)
        + (SELECT COUNT(*) FROM player_profiles WHERE instr(display_name, '~') > 0) + 1
),
free AS (
    SELECT g.group_id, n, ROW_NUMBER() OVER (PARTITION BY g.group_id ORDER BY n) AS slot
    FROM (SELECT DISTINCT group_id FROM losers) AS g
    CROSS JOIN numbers
    WHERE NOT EXISTS (
        SELECT 1 FROM player_profiles AS p
        WHERE p.display_name = g.group_id || '~' || n COLLATE NOCASE)
)
SELECT l.player_name, l.display_name || '~' || f.n
FROM losers AS l JOIN free AS f ON f.group_id = l.group_id AND f.slot = l.slot;

UPDATE player_profiles
SET display_name = (SELECT f.new_display_name FROM display_name_fix__v7 AS f
                    WHERE f.player_name = player_profiles.player_name)
WHERE player_name IN (SELECT player_name FROM display_name_fix__v7);

DROP TABLE display_name_fix__v7;
DROP INDEX ix_player_profiles_display_name__v7;

CREATE UNIQUE INDEX ux_player_profiles_display_name
    ON player_profiles (display_name COLLATE NOCASE);
