package main

import "net/http"

var roleRank = map[string]int{
	"player":     1,
	"admin":      2,
	"superadmin": 3,
}

func hasRole(userRole, minRole string) bool {
	return roleRank[userRole] >= roleRank[minRole]
}

// requireRole оборачивает обработчик: пускает дальше только если у пользователя
// роль minRole или выше (player < admin < superadmin). Понадобится, когда появятся
// защищённые эндпоинты — например управление статьями или назначение ролей.
func requireRole(minRole string, next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		cookie, err := r.Cookie("token")
		if err != nil {
			writeError(w, http.StatusUnauthorized, "Не авторизован")
			return
		}

		claims, err := parseToken(cookie.Value)
		if err != nil {
			writeError(w, http.StatusUnauthorized, "Не авторизован")
			return
		}

		if !hasRole(claims.Role, minRole) {
			writeError(w, http.StatusForbidden, "Недостаточно прав")
			return
		}

		next(w, r)
	}
}
