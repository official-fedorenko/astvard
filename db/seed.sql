INSERT INTO games (name, slug, description) VALUES
  ('Valheim', 'valheim', 'Кооперативное выживание в мире норвежской мифологии.'),
  ('Minecraft', 'minecraft', 'Песочница про добычу ресурсов и постройки.'),
  ('Counter-Strike 2', 'cs2', 'Командный шутер от Valve.')
ON CONFLICT (slug) DO NOTHING;

INSERT INTO servers (game_id, name, host, port, description)
SELECT id, 'Astvard metalVportal', '72.61.139.115', 2462, 'Личная карта metalVportal.'
FROM games WHERE slug = 'valheim'
ON CONFLICT DO NOTHING;

INSERT INTO servers (game_id, name, host, port, description)
SELECT id, 'Astvard Minecraft SMP', 'mc.astvard.local', 25565, 'Ванильный SMP без читов.'
FROM games WHERE slug = 'minecraft'
ON CONFLICT DO NOTHING;

INSERT INTO servers (game_id, name, host, port, description)
SELECT id, 'Astvard CS2 #1', 'cs2.astvard.local', 27015, 'Основной сервер CS2.'
FROM games WHERE slug = 'cs2'
ON CONFLICT DO NOTHING;

INSERT INTO articles (title, slug, content) VALUES
  ('Добро пожаловать на Astvard', 'welcome', 'Это первая статья портала. Скоро тут будет больше контента.')
ON CONFLICT (slug) DO NOTHING;
