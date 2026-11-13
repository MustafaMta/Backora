-- Synthetic legacy Backora (Laravel) fixture. Contains NO real data or credentials.
-- Mirrors the production MariaDB schema shape (including columns missing in some versions).
SET NAMES utf8mb4;
SET time_zone = '+00:00';

CREATE TABLE users (
  id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
  name varchar(255) NOT NULL, user_id varchar(255) NOT NULL, email varchar(255) NOT NULL UNIQUE,
  join_date varchar(255) NULL, last_login varchar(255) NULL, phone_number varchar(255) NULL, status varchar(255) NULL,
  role_name varchar(255) NULL, avatar varchar(255) NULL, position varchar(255) NULL, department varchar(255) NULL,
  email_verified_at timestamp NULL, password varchar(255) NOT NULL, remember_token varchar(100) NULL,
  created_at timestamp NULL, updated_at timestamp NULL);

CREATE TABLE servers (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, server_name varchar(255) NOT NULL, server_ip varchar(255) NOT NULL,
  ssh_user varchar(255) NOT NULL DEFAULT 'root', ssh_port int NOT NULL DEFAULT 22, private_key text NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE worker_settings (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, server_ip varchar(255) NOT NULL, user varchar(255) NOT NULL,
  worker_ssh_private_key text NOT NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE ssh_settings (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, ssh_key text NOT NULL, private_key text NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE nfs_settings (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, nfs_host varchar(255) NOT NULL, nfs_path varchar(255) NOT NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE variables (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, name varchar(255) NOT NULL UNIQUE, value text NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE templates (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, name varchar(255) NOT NULL, category varchar(255) NULL, script text NOT NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE s3_settings (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, name varchar(255) NOT NULL, `key` varchar(255) NOT NULL, secret varchar(255) NOT NULL,
  region varchar(255) NOT NULL, bucket varchar(255) NOT NULL, endpoint varchar(255) NOT NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE smtp_settings (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, host varchar(255) NOT NULL, port varchar(255) NOT NULL, username varchar(255) NOT NULL,
  password varchar(255) NOT NULL, from_address varchar(255) NOT NULL, template text NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE teams_settings (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, webhook_url varchar(512) NOT NULL, template text NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE notification_settings (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, notification_emails longtext NOT NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE backup_jobs (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, name varchar(255) NOT NULL, category varchar(255) NULL, script_name varchar(255) NOT NULL,
  status enum('active','inactive') NOT NULL DEFAULT 'inactive', schedule_at varchar(255) NULL, created_at timestamp NULL, updated_at timestamp NULL,
  server_id bigint unsigned NOT NULL, s3_config_id bigint unsigned NULL, s3_config_added_at timestamp NULL, retention_days decimal(10,4) NOT NULL);
CREATE TABLE backup_job_executions (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, queue_job_id varchar(255) NULL, backup_job_id bigint unsigned NOT NULL,
  server_id bigint unsigned NOT NULL, job_name varchar(255) NOT NULL, category varchar(255) NULL, script_name varchar(255) NOT NULL,
  status enum('pending','running','completed','failed','cancelled') NOT NULL DEFAULT 'pending', started_at timestamp NULL, completed_at timestamp NULL,
  output text NULL, error_message text NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE upload_to_s3_job_executions (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, s3_config_id bigint unsigned NOT NULL, server_id bigint unsigned NULL,
  backup_job_execution_id bigint unsigned NULL, nfs_path varchar(255) NOT NULL, status enum('pending','running','completed','failed') NOT NULL DEFAULT 'pending',
  started_at timestamp NULL, completed_at timestamp NULL, log longtext NULL, error_message longtext NULL, pid int unsigned NULL,
  created_at timestamp NULL, updated_at timestamp NULL, total_size varchar(50) NULL);
CREATE TABLE restore_to_nfs_job_executions (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, upload_to_s3_job_execution_id bigint unsigned NOT NULL,
  server_id bigint unsigned NOT NULL, status varchar(255) NOT NULL DEFAULT 'pending', started_at timestamp NULL, completed_at timestamp NULL,
  log text NULL, error_message text NULL, pid varchar(255) NULL, restored_path varchar(255) NULL, created_at timestamp NULL, updated_at timestamp NULL);
CREATE TABLE api_tokens (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, user_id bigint unsigned NOT NULL, name varchar(255) NOT NULL DEFAULT 'curl',
  token_hash varchar(64) NOT NULL UNIQUE, last_used_at timestamp NULL, expires_at timestamp NULL, created_at timestamp NULL, updated_at timestamp NULL);
-- Laravel infrastructure / template leftovers that must be ignored.
CREATE TABLE jobs (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, queue varchar(255) NOT NULL, payload longtext NOT NULL, attempts tinyint unsigned NOT NULL,
  reserved_at int unsigned NULL, available_at int unsigned NOT NULL, created_at int unsigned NOT NULL);
CREATE TABLE roles_permissions (id bigint unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY, permissions_name varchar(255) NULL);

-- bcrypt hash of a throwaway synthetic password (not used anywhere real)
INSERT INTO users VALUES
 (3,'Ada Admin','KH-2025-07-00001','ada@example.test',NULL,NULL,NULL,NULL,'super admin',NULL,NULL,NULL,NULL,'$2y$10$abcdefghijklmnopqrstuuJZ7YqC3ZxwFq9yQw6qkK1m5HcXK3fDe',NULL,'2025-07-18 10:00:00','2025-07-20 10:00:00'),
 (7,'Vic Viewer','KH-2025-07-00002','vic@example.test',NULL,NULL,NULL,'Inactive','viewer',NULL,NULL,NULL,NULL,'$2y$10$abcdefghijklmnopqrstuuJZ7YqC3ZxwFq9yQw6qkK1m5HcXK3fDe',NULL,'2025-07-18 11:00:00','2025-07-18 11:00:00'),
 (9,'Hal HR','KH-2025-07-00003','hal@example.test',NULL,NULL,NULL,'Active','Employee',NULL,NULL,NULL,NULL,'$2y$10$abcdefghijklmnopqrstuuJZ7YqC3ZxwFq9yQw6qkK1m5HcXK3fDe',NULL,'2025-07-18 12:00:00','2025-07-18 12:00:00');

INSERT INTO servers VALUES
 (11,'db-01','10.0.0.11','root',22,NULL,'2025-07-18 10:00:00','2025-07-18 10:00:00'),
 (12,'db-01','10.0.0.12','backup',2222,'-----BEGIN OPENSSH PRIVATE KEY-----\nFAKE\n-----END OPENSSH PRIVATE KEY-----','2025-07-18 10:00:00','2025-07-18 10:00:00'),
 (15,'pg-01','pg-01.example.test','root',22,'','2025-07-18 10:00:00','2025-07-18 10:00:00');

INSERT INTO worker_settings VALUES (1,'10.0.0.2','backora','-----BEGIN OPENSSH PRIVATE KEY-----\nFAKE-WORKER\n-----END OPENSSH PRIVATE KEY-----','2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO ssh_settings VALUES (1,'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE backora@test','-----BEGIN OPENSSH PRIVATE KEY-----\nFAKE-DEFAULT\n-----END OPENSSH PRIVATE KEY-----','2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO nfs_settings VALUES (1,'nfs.example.test:/backora','/BackoraDB','2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO variables VALUES (1,'mysql_user','backup','2025-07-18 10:00:00','2025-07-18 10:00:00'), (2,'mysql_password','fake-variable-secret','2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO templates VALUES
 (1,'mariabackup','MariaDB','#!/bin/bash\r\necho "{JOB_NAME}"\r\n','2025-07-18 10:00:00','2025-07-18 10:00:00'),
 (5,'pg_dump','PostgrSQL','#!/bin/bash\necho "{JOB_NAME} {NFS_PATH}"\n','2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO s3_settings VALUES (2,'Primary S3','FAKEACCESSKEY','fake-s3-secret','us-east-1','backups','https://s3.example.test','2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO smtp_settings VALUES (1,'smtp.example.test','465','mailer','fake-smtp-password','backora@example.test',NULL,'2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO teams_settings VALUES (1,'https://example.webhook.office.com/webhookb2/fake','{"@type":"MessageCard","title":"{{job_name}}"}','2025-07-18 10:00:00','2025-07-18 10:00:00');
INSERT INTO notification_settings VALUES (1,'["ops@example.test","dba@example.test"]','2025-07-18 10:00:00','2025-07-18 10:00:00');

INSERT INTO backup_jobs VALUES
 (21,'orders-db','MariaDB','mariabackup','active','0 2 * * *','2025-07-20 10:00:00','2025-07-20 10:00:00',11,2,'2025-07-20 10:00:00',7.0000),
 (22,'bad name!','MariaDB','mariabackup','active','0 3 * * *','2025-07-20 10:00:00','2025-07-20 10:00:00',12,NULL,NULL,0.0417),
 (23,'pg-main','PostgrSQL','pg_dump','active','61 * * * *','2025-07-20 10:00:00','2025-07-20 10:00:00',15,2,NULL,14.0000),
 (24,'ghost-job','MongoDB','mongodump','inactive',NULL,'2025-07-20 10:00:00','2025-07-20 10:00:00',15,NULL,NULL,3.0000);

INSERT INTO backup_job_executions VALUES
 (101,'55',21,11,'orders-db','MariaDB','mariabackup','completed','2025-08-01 02:00:00','2025-08-01 02:05:00','{"pid":"123","log_path":"/tmp/x","final_log":"backup ok"}',NULL,'2025-08-01 02:00:00','2025-08-01 02:05:00'),
 (102,NULL,21,11,'orders-db','MariaDB','mariabackup','running','2025-08-02 02:00:00',NULL,NULL,NULL,'2025-08-02 02:00:00','2025-08-02 02:00:00'),
 (103,NULL,23,15,'pg-main','PostgrSQL','pg_dump','failed','2025-08-03 02:00:00','2025-08-03 02:01:00',NULL,'pg_dump: connection refused','2025-08-03 02:00:00','2025-08-03 02:01:00'),
 (104,NULL,23,15,'pg-main','PostgrSQL','pg_dump','pending','2025-08-04 02:00:00',NULL,NULL,NULL,'2025-08-04 02:00:00','2025-08-04 02:00:00'),
 (105,NULL,99,15,'deleted-job','MariaDB','mariabackup','completed','2025-08-05 02:00:00','2025-08-05 02:03:00',NULL,NULL,'2025-08-05 02:00:00','2025-08-05 02:03:00'),
 (106,NULL,21,11,'orders-db','MariaDB','mariabackup','cancelled','2025-08-06 02:00:00','2025-08-06 02:01:00',NULL,'Job was cancelled by user','2025-08-06 02:00:00','2025-08-06 02:01:00');

INSERT INTO upload_to_s3_job_executions VALUES
 (201,2,11,101,'/BackoraDB','completed','2025-08-01 04:10:00','2025-08-01 04:30:00','Transferred: 1.5 GiB',NULL,4321,'2025-08-01 04:05:00','2025-08-01 04:30:00','1.5 GiB'),
 (202,2,11,102,'/BackoraDB','running','2025-08-02 04:10:00',NULL,NULL,NULL,4322,'2025-08-02 04:05:00','2025-08-02 04:05:00','55M'),
 (203,2,15,105,'/BackoraDB/deleted-job','failed','2025-08-05 04:10:00','2025-08-05 04:11:00',NULL,'rclone failed',NULL,'2025-08-05 04:05:00','2025-08-05 04:11:00',NULL);

INSERT INTO restore_to_nfs_job_executions VALUES
 (301,201,11,'completed','2025-08-07 09:00:00','2025-08-07 09:10:00','restored',NULL,'999','/BackoraDB/orders-db','2025-08-07 09:00:00','2025-08-07 09:10:00'),
 (302,201,11,'running','2025-08-08 09:00:00',NULL,NULL,NULL,'1000','/BackoraDB/orders-db','2025-08-08 09:00:00','2025-08-08 09:00:00'),
 (303,999,11,'completed','2025-08-09 09:00:00','2025-08-09 09:10:00',NULL,NULL,NULL,NULL,'2025-08-09 09:00:00','2025-08-09 09:10:00');

INSERT INTO api_tokens VALUES (1,3,'curl','0000000000000000000000000000000000000000000000000000000000000000',NULL,'2025-08-01 00:00:00','2025-08-01 00:00:00','2025-08-01 00:00:00');
INSERT INTO jobs VALUES (1,'default','{}',0,NULL,0,0);
