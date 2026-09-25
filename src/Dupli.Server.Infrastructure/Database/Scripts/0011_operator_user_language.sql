-- UI language preference, set by the operator from the topbar; NULL means unset (fall back to the browser).

ALTER TABLE operator_user ADD COLUMN language text NULL;
