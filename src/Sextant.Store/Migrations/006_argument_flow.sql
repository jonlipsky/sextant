CREATE TABLE IF NOT EXISTS argument_flow (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    call_graph_id       INTEGER NOT NULL REFERENCES call_graph(id) ON DELETE CASCADE,
    parameter_ordinal   INTEGER NOT NULL,
    parameter_name      TEXT NOT NULL,
    argument_expression TEXT NOT NULL,
    argument_kind       TEXT NOT NULL,
    source_symbol_fqn   TEXT,
    last_indexed_at     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_argflow_callgraph ON argument_flow(call_graph_id);
CREATE INDEX IF NOT EXISTS ix_argflow_source ON argument_flow(source_symbol_fqn);

CREATE TABLE IF NOT EXISTS return_flow (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    call_graph_id       INTEGER NOT NULL REFERENCES call_graph(id) ON DELETE CASCADE,
    destination_kind    TEXT NOT NULL,
    destination_variable TEXT,
    destination_symbol_fqn TEXT,
    last_indexed_at     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_retflow_callgraph ON return_flow(call_graph_id);
CREATE INDEX IF NOT EXISTS ix_retflow_destination ON return_flow(destination_symbol_fqn);
