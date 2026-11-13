-- Scientific (generic) name of a product (owner request 2026-11-13), shown under the name in the app and the panel. Optional.
ALTER TABLE products ADD COLUMN scientific_name text CHECK (char_length(scientific_name) BETWEEN 1 AND 150);
