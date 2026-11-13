-- The leader of a team (owner request 2026-11-13): one user per team, who must be a member of the team and hold the role Supervisor (the server checks both when
-- the leader is set; the team list only reports a leader who still is a member with that role). A supervisor still sees the whole region of the team,
-- the leader is the person responsible for this team.
ALTER TABLE teams ADD COLUMN leader_user_id uuid;
ALTER TABLE teams ADD CONSTRAINT teams_leader_fk FOREIGN KEY (tenant_id, leader_user_id) REFERENCES users(tenant_id, id);
