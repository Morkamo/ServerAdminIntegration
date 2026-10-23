CREATE SCHEMA IF NOT EXISTS bot;
CREATE TABLE IF NOT EXISTS bot.command_permissions (
    guild_id bigint NOT NULL CHECK (guild_id > 0),
    command varchar(100) NOT NULL,
    everyone boolean NOT NULL DEFAULT false,
    role_ids bigint[] NOT NULL DEFAULT '{}',
    PRIMARY KEY (guild_id, command)
);
ALTER TABLE bot.command_permissions ALTER COLUMN everyone SET DEFAULT false;
