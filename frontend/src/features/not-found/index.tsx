import { FileQuestion } from "lucide-react";
import { useTranslation } from "react-i18next";
import { EmptyState, Button } from "../../components/ui";
import { useNavigate } from "react-router-dom";

export default function NotFound() {
  const navigate = useNavigate();
  const { t } = useTranslation("common");

  return (
    <div className="flex items-center justify-center h-full min-h-[60vh]">
      <EmptyState
        icon={<FileQuestion className="size-12" strokeWidth={1.5} />}
        title={t("pageNotFound")}
        description={t("pageNotFoundDesc")}
        action={
          <Button variant="secondary" onClick={() => navigate("/")}>
            {t("backToDashboard")}
          </Button>
        }
      />
    </div>
  );
}
