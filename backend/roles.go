package main

import (
	"context"
	"net/http"
)

var roleRank = map[string]int{
	"player":     1,
	"admin":      2,
	"superadmin": 3,
}

func hasRole(userRole, minRole string) bool {
	return roleRank[userRole] >= roleRank[minRole]
}

type contextKey string

const claimsContextKey contextKey = "claims"

func claimsFromContext(r *http.Request) *customClaims {
	claims, _ := r.Context().Value(claimsContextKey).(*customClaims)
	return claims
}

// requireRole оборачивает обработчик: пускает дальше только если у пользователя
// роль minRole или выше (player < admin < superadmin), и кладёт claims в контекст
// запроса, чтобы обработчик знал, кто именно его вызвал (например для author_id).
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

		ctx := context.WithValue(r.Context(), claimsContextKey, claims)
		next(w, r.WithContext(ctx))
	}
}
