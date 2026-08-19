import { FileQuestion } from "lucide-react";
import { EmptyState, Button } from "../../components/ui";
import { useNavigate } from "react-router-dom";

export default function NotFound() {
  const navigate = useNavigate();

  return (
    <div className="flex items-center justify-center h-full min-h-[60vh]">
      <EmptyState
        icon={<FileQuestion className="size-12" strokeWidth={1.5} />}
        title="Page not found"
        description="The page you are looking for does not exist or has been moved."
        action={
          <Button variant="secondary" onClick={() => navigate("/")}>
            Back to Dashboard
          </Button>
        }
      />
    </div>
  );
}
