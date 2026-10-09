-- Who gets CommissionBoot emails (errors + successful downloads).
-- recipient_type_id: 1 = To, 2 = Cc, 3 = Bcc
-- is_active:         1 = send, 0 = stop sending (no need to delete the row)

USE commissionReport;

CREATE TABLE IF NOT EXISTS email_recipients (
    id                INT          NOT NULL AUTO_INCREMENT,
    name              VARCHAR(100) NOT NULL,
    email             VARCHAR(255) NOT NULL,
    recipient_type_id INT          NOT NULL DEFAULT 1,
    is_active         TINYINT(1)   NOT NULL DEFAULT 1,
    created_at        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id)
);

INSERT INTO email_recipients (name, email, recipient_type_id, is_active)
SELECT 'Haseeb Uddin', 'haseeb.uddin@techno-communications.com', 1, 1
WHERE NOT EXISTS (SELECT 1 FROM email_recipients WHERE email = 'haseeb.uddin@techno-communications.com');

-- Turn someone off / on:
-- UPDATE email_recipients SET is_active = 0 WHERE email = 'haseeb.uddin@techno-communications.com';
