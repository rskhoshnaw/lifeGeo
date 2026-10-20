-- Product photos (plan step 4.40): one picture per product, kept in the database (so the normal backup has it and no extra disk volume is needed). Up to 1 MB, JPEG, PNG or WEBP.
-- Every upload makes a new random photo_key; the picture is fetched by that key (GET /product-photos/{key}, public: a product picture is not private and the key cannot be guessed),
-- so the app and the panel can show it as a plain image address and it can be cached for good. Because of that read, this table has no row level security;
-- every write names the company explicitly.
CREATE TABLE product_photos (
    product_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    photo_key text NOT NULL UNIQUE CHECK (photo_key ~ '^[0-9a-f]{32}$'),
    content_type text NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    data bytea NOT NULL CHECK (octet_length(data) BETWEEN 1 AND 1048576),
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id)
);
