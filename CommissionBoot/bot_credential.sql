-- Run once on the dailyCommission database the bot loads into.
-- bot_credential: RTPOS login. is_active = 0 stops the bot.
-- email_setting:  who gets the bot email (1 = To, 2 = Cc, 3 = Bcc). is_active = 0 stops that person's email.
USE dailyCommission;

CREATE TABLE IF NOT EXISTS bot_credential (
    id INT NOT NULL AUTO_INCREMENT,
    username VARCHAR(255) NOT NULL,
    password VARCHAR(255) NOT NULL,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS email_setting (
    id INT NOT NULL AUTO_INCREMENT,
    name VARCHAR(255) NOT NULL,
    email VARCHAR(255) NOT NULL,
    recipient_type_id INT NOT NULL DEFAULT 1,
    is_active TINYINT(1) NOT NULL DEFAULT 1,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (id)
) ENGINE=InnoDB;

INSERT INTO bot_credential (username, password, is_active) VALUES ('RTPOS_USERNAME', 'RTPOS_PASSWORD', 1);
INSERT INTO email_setting (name, email, recipient_type_id, is_active) VALUES ('NAME', 'EMAIL@EXAMPLE.COM', 1, 1);
