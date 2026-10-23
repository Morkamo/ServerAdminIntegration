\set ON_ERROR_STOP on
\getenv server_admin_password SERVERADMIN_DB_PASSWORD
\if :{?server_admin_password}
\else
    \warn Set SERVERADMIN_DB_PASSWORD before running bootstrap.sql.
    \quit 1
\endif
SELECT length(:'server_admin_password') >= 16 AS password_valid \gset
\if :password_valid
\else
    \warn SERVERADMIN_DB_PASSWORD must contain at least 16 characters.
    \quit 1
\endif

CREATE ROLE server_admin_bot LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'server_admin_password';
CREATE DATABASE server_admin OWNER server_admin_bot;
REVOKE ALL ON DATABASE server_admin FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE server_admin TO server_admin_bot;
\connect server_admin
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
