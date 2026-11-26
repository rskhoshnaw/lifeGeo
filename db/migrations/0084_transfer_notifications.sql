-- Plan step 5.61: the people who work in a warehouse are told when goods are on the way to it (transfer.sent), when what they sent has arrived (transfer.received) and when a transfer
-- that was coming to them is cancelled (transfer.cancelled). The notification carries the transfer id; the phone and the panel write the text in the language of the user.
ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check
    CHECK (kind IN ('customer.profile_changed', 'customer.location_changed', 'manager.message', 'tracking.stopped', 'feedback.new', 'feedback.reply', 'customer.owner_changed', 'team.shift_stopped',
                    'return.new', 'return.decided', 'transfer.sent', 'transfer.received', 'transfer.cancelled'));
