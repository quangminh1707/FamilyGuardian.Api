-- Add Google ID columns to proxy_ip_mappings table
ALTER TABLE family_guardian.proxy_ip_mappings 
ADD COLUMN google_id VARCHAR(255) NULL,
ADD COLUMN google_email VARCHAR(255) NULL;
