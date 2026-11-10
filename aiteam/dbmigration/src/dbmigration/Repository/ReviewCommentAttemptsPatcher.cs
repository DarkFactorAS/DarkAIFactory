using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

/// <summary>
/// Review comment attempt tracking. Only adds a new table; existing tables are untouched.
/// The patch is idempotent (CREATE TABLE IF NOT EXISTS) so it can recover from a failed history write.
/// </summary>
public static class ReviewCommentAttemptsPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "ReviewCommentAttempts";
        patcher.Init(system);

        patcher.Patch(system, 1,
            """
            CREATE TABLE IF NOT EXISTS `review_comment_attempts` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `pull_request_id` BIGINT NOT NULL,
                `github_comment_id` BIGINT NOT NULL,
                `attempts` INT NOT NULL DEFAULT 0,
                `status` VARCHAR(32) NOT NULL DEFAULT 'Pending',
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                `last_error` TEXT NULL,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_review_comment_attempts_pr_comment` (`pull_request_id`, `github_comment_id`),
                CONSTRAINT `ck_review_comment_attempts_status` CHECK (
                    `status` IN ('Pending', 'InProgress', 'Resolved', 'Failed')),
                CONSTRAINT `fk_review_comment_attempts_pull_request` FOREIGN KEY (`pull_request_id`)
                    REFERENCES `pull_requests` (`id`) ON DELETE CASCADE
            )
            """);
        return patcher.Successful();
    }
}
