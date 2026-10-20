-- The same neighbourhood must not exist twice because of how it was typed (plan step 4.45): Arabic and Persian/Kurdish keyboards write the same letters with different
-- characters. place_name_norm() makes a name comparable: lower case; Arabic yeh (ي ى) -> Persian yeh (ی); Arabic kaf (ك) -> Persian kaf (ک); the Kurdish heh with two eyes (ھ) -> heh (ه);
-- hamza forms of alef (أ إ ٱ) -> alef (ا); Arabic-Indic and Persian digits -> 0-9; tatweel, diacritics and zero-width joiners removed; runs of spaces -> one space.
-- The Kurdish e (ە) is a different letter from heh and is left alone. The API uses it to refuse a name that only differs in these ways (and the import reuses the place that exists).
-- The existing unique index (lower(btrim(name))) stays; this check is added in the application so old data with look-alike names does not block the migration.
CREATE FUNCTION place_name_norm(t text) RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT btrim(regexp_replace(
        regexp_replace(
            translate(lower(t),
                'يىكھ٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹أإٱ' || E'ـ‌‍',
                'ییکه01234567890123456789ااا'),
            E'[ً-ٰٟ]', '', 'g'),
        E'\\s+', ' ', 'g'))
$$;
