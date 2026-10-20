-- Doctor details on a customer (plan step 4.27): clinic / medical complex name, specialty (one of a fixed list, checked by the API),
-- whether the doctor cooperates, whether the doctor is a dealer, and a class A+ / A / B / C. All optional; the doctor's name is the customer name,
-- the optional mobile is `phone`, visit days and times are the existing visit windows, notes exist since 0045.
ALTER TABLE customers ADD COLUMN clinic_name text CHECK (char_length(clinic_name) BETWEEN 1 AND 150);
ALTER TABLE customers ADD COLUMN specialty text CHECK (char_length(specialty) BETWEEN 1 AND 40);
ALTER TABLE customers ADD COLUMN cooperates boolean;
ALTER TABLE customers ADD COLUMN is_dealer boolean;
ALTER TABLE customers ADD COLUMN customer_class text CHECK (customer_class IN ('A+', 'A', 'B', 'C'));
