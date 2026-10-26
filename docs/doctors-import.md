# Doctor list for import (template: `docs/doctors-import-template.csv`)

One row per doctor, UTF-8 CSV (Excel: Save as "CSV UTF-8"). Only `name` is required; leave any other cell empty when unknown.

| Column | Meaning | Allowed values |
| --- | --- | --- |
| name | Doctor name (the customer name) | text |
| clinic_name | Clinic or medical complex | text |
| specialty | Specialty | orthopedics, neuro_spine, neurology, psychiatry, pediatrics, women_obstetrics, surgery, kidney_urology, ent, other |
| cooperates | Cooperates with us | yes / no / empty |
| dealer | Dealer | yes / no / empty |
| class | Class | A+, A, B, C or empty |
| phone, phone2 | Phones (07xx xxx xxxx) | text |
| city, neighbourhood, street | Places (matched to the place tree; unknown names are added) | text |
| address | Address note | text |
| latitude, longitude | GPS position, both or none | numbers (36.2201, 43.9924) |
| notes | Note | text |
| visitor_email | The visitor who owns the doctor (must be an existing user) | e-mail or empty |

Doctors without a location are imported as customers without a position; the visitor can set it in the app when visiting.
The importer itself is plan step 5.9; this file fixes the columns so the list can be prepared now.
